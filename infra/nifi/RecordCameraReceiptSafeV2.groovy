import groovy.json.JsonSlurper
import java.nio.charset.StandardCharsets
import java.sql.SQLException
import java.sql.SQLTransientException
import java.sql.SQLRecoverableException
import java.time.OffsetDateTime
import org.apache.nifi.processor.io.InputStreamCallback

// CTL.reporting is a Controller Service reference, NOT SQL.reporting.
// Connection acquisition must happen inside this script's guarded DB section.
// SQL is fixed; neither FlowFile content nor attributes can supply a statement.
def ff = session.get()
if (ff == null) return

def values
String expectedJdbcBase
int retries = 0
boolean retryCounterValid = false
try {
    expectedJdbcBase = context.getProperty('ReportingJdbcBase').value
    if (expectedJdbcBase == null || !(expectedJdbcBase ==~ /jdbc:postgresql:\/\/[A-Za-z0-9.-]+:5432\/aura_reporting/))
        throw new IllegalArgumentException()
    if (ff.getAttribute('camera.reporting.contract') != 'receipt-v1'
        || ff.getAttribute('mime.type') != 'application/json'
        || ff.getSize() < 1 || ff.getSize() > 8192) throw new IllegalArgumentException()
    def counter = ff.getAttribute('camera.reporting.retries')
    if (counter == null || !(counter ==~ /(?:[0-9]|10)/)) throw new IllegalArgumentException()
    retries = Integer.parseInt(counter)
    retryCounterValid = true
    byte[] payload
    session.read(ff, { input ->
        def output = new ByteArrayOutputStream()
        byte[] buffer = new byte[1024]
        int count
        while ((count = input.read(buffer)) != -1) {
            if (output.size() + count > 8192) throw new IllegalArgumentException()
            output.write(buffer, 0, count)
        }
        payload = output.toByteArray()
    } as InputStreamCallback)
    def body = new JsonSlurper().parseText(new String(payload, StandardCharsets.UTF_8))
    def expectedKeys = ['requestId','deviceId','batchId','imageCount',
        'sourceAcceptedAt','correlationId','analysisId','processingStatus'] as Set
    if (!(body instanceof Map)) throw new IllegalArgumentException()
    def uuid = { value ->
        if (!(value instanceof String) || !(value ==~ /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/)
            || UUID.fromString(value) == new UUID(0L, 0L)) throw new IllegalArgumentException()
        value
    }
    def request = uuid(body.requestId)
    def batch = uuid(body.batchId)
    uuid(body.analysisId)
    def imgHash = body.imageSha256 ?: body.image_hash_sha256
    if (!(body.deviceId instanceof String) || !(body.deviceId ==~ /[A-Za-z0-9][A-Za-z0-9._:-]{0,127}/)
        || !(imgHash instanceof String) || !(imgHash ==~ /[0-9a-f]{64}/)
        || !(body.correlationId instanceof String) || !(body.correlationId ==~ /[A-Za-z0-9._-]{1,64}/)
        || !(body.imageCount instanceof Integer) || body.imageCount != 1
        || !(body.sourceAcceptedAt instanceof String)
        || !(body.processingStatus in ['QUEUED','DUPLICATE'])) throw new IllegalArgumentException()
    def accepted = OffsetDateTime.parse(body.sourceAcceptedAt).toInstant()
    if (ff.getAttribute('camera.receipt.request_id') != request
        || ff.getAttribute('camera.receipt.batch_id') != batch
        || ff.getAttribute('filename') != request + '.receipt.json') throw new IllegalArgumentException()
    values = [request, body.deviceId, imgHash, batch, '1', body.sourceAcceptedAt, body.correlationId]
    values.eachWithIndex { value, index ->
        if (ff.getAttribute('sql.args.' + (index + 1) + '.type') != '12'
            || ff.getAttribute('sql.args.' + (index + 1) + '.value') != value) throw new IllegalArgumentException()
    }
    if (ff.attributes.keySet().any { it.startsWith('sql.args.') && !(it ==~ /sql\.args\.[1-7]\.(?:type|value)/) })
        throw new IllegalArgumentException()
} catch (Exception ignored) {
    ff = session.putAllAttributes(ff, [
        'camera.reporting.writer.version':'receipt-jdbc-v2',
        'camera.reporting.outcome':'HOLD',
        'reporting.status':'HOLD',
        'camera.reporting.reason':'INVALID_RECEIPT_CONTRACT',
        'quarantine.reason':'INVALID_RECEIPT_CONTRACT',
        'camera.reporting.commit_state':'NOT_ATTEMPTED',
        'camera.reporting.sqlstate':'NONE',
        'camera.reporting.last_retry_count':retryCounterValid ? Integer.toString(retries) : 'INVALID',
        'camera.reporting.cleanup_failed':'false'])
    session.transfer(ff, REL_FAILURE)
    return
}

def connection = null
def statement = null
boolean committed = false
boolean commitAttempted = false
boolean cleanupFailed = false
String outcome = 'HOLD'
String reason = 'REPORTING_UNCLASSIFIED_FAILURE'
String sqlState = 'NONE'

try {
    if (ff.getAttribute('camera.simulate.transient') == 'true' && retries == 0) {
        throw new SQLTransientException('Simulated transient connection timeout', '08006')
    }
    connection = CTL.reporting.getConnection(ff.attributes)
    def metadata = connection.getMetaData()
    if (metadata.getURL().split('\\?', 2)[0] != expectedJdbcBase
        || metadata.getUserName() != 'aura_reporting_writer') throw new IllegalStateException()
    connection.setAutoCommit(false)
    statement = connection.prepareStatement('CALL reporting.record_camera_receipt(CAST(? AS uuid),CAST(? AS text),CAST(? AS text),CAST(? AS uuid),CAST(? AS integer),CAST(? AS timestamptz),CAST(? AS text));')
    statement.setQueryTimeout(10)
    values.eachWithIndex { value, index -> statement.setString(index + 1, value) }
    statement.execute()
    commitAttempted = true
    connection.commit()
    committed = true
    outcome = 'RECORDED'
    reason = 'RECEIPT_COMMITTED'
} catch (Exception error) {
    def pending = new ArrayDeque()
    def seen = Collections.newSetFromMap(new IdentityHashMap())
    def states = []
    boolean transientFailure = false
    pending.add(error)
    int examined = 0
    while (!pending.isEmpty() && examined++ < 32) {
        def item = pending.removeFirst()
        if (!seen.add(item)) continue
        if (item instanceof SQLException) {
            def state = item.getSQLState()
            if (state != null && state ==~ /[0-9A-Z]{5}/) states.add(state)
            if (item instanceof SQLTransientException || item instanceof SQLRecoverableException) transientFailure = true
            if (item.nextException != null) pending.add(item.nextException)
        }
        if (item instanceof java.net.ConnectException || item instanceof java.net.UnknownHostException
            || item instanceof java.net.SocketTimeoutException) transientFailure = true
        if (item.cause != null) pending.add(item.cause)
    }
    boolean permanent = states.any { it.startsWith('22') || it.startsWith('23') || it.startsWith('28')
        || it.startsWith('42') || it == 'P0001' }
    transientFailure = transientFailure || states.any { it.startsWith('08') || it.startsWith('53')
        || it in ['40001','40P01','55P03','57014','57P01','57P02','57P03'] }
    sqlState = states ? states[0] : 'NONE'
    outcome = !permanent && transientFailure ? 'RETRY' : 'HOLD'
    reason = outcome == 'RETRY' ? 'REPORTING_TRANSIENT_FAILURE' : 'REPORTING_RECONCILE_REQUIRED'
} finally {
    if (connection != null && !committed) {
        try { connection.rollback() } catch (Exception ignored) { cleanupFailed = true }
    }
    if (statement != null) {
        try { statement.close() } catch (Exception ignored) { cleanupFailed = true }
    }
    if (connection != null) {
        try { connection.close() } catch (Exception ignored) { cleanupFailed = true }
    }
}

if (!committed && commitAttempted) {
    outcome = 'HOLD'
    reason = 'REPORTING_COMMIT_UNKNOWN'
} else if (cleanupFailed) {
    outcome = 'HOLD'
    reason = committed ? 'REPORTING_COMMITTED_CLEANUP_FAILED' : 'REPORTING_CLEANUP_FAILED'
}

String reportingStatus = outcome == 'RECORDED' ? 'COMMITTED' : outcome
String quarantineReason = outcome == 'HOLD' ? (sqlState == '23505' ? 'CONFLICTING_RECEIPT_SQLSTATE_23505' : reason) : ''

ff = session.putAllAttributes(ff, [
    'camera.reporting.writer.version':'receipt-jdbc-v2',
    'camera.reporting.outcome':outcome,
    'reporting.status':reportingStatus,
    'camera.reporting.reason':reason,
    'quarantine.reason':quarantineReason,
    'camera.reporting.sqlstate':sqlState,
    'camera.reporting.last_retry_count':Integer.toString(retries),
    'camera.reporting.commit_state':committed ? 'COMMITTED' : commitAttempted ? 'UNKNOWN' : 'NOT_COMMITTED',
    'camera.reporting.cleanup_failed':Boolean.toString(cleanupFailed)])
session.transfer(ff, outcome == 'RECORDED' && committed && !cleanupFailed ? REL_SUCCESS : REL_FAILURE)
