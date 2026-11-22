import groovy.json.JsonOutput
import groovy.json.JsonSlurper
import java.nio.charset.StandardCharsets
import org.apache.nifi.processor.exception.ProcessException
import org.apache.nifi.processor.io.InputStreamCallback
import org.apache.nifi.processor.io.OutputStreamCallback

def ff = session.get()
if (ff == null) return

def post = { String url ->
    def conn = new URI(url).toURL().openConnection() as HttpURLConnection
    conn.instanceFollowRedirects = false
    conn.connectTimeout = 5000
    conn.readTimeout = 30000
    conn.requestMethod = 'POST'
    conn.doOutput = true
    conn
}
def readJson = { conn ->
    conn.inputStream.withCloseable { stream ->
        def bytes = stream.readNBytes(8193)
        if (bytes.length > 8192) throw new IOException('Response limit')
        new JsonSlurper().parseText(new String(bytes, StandardCharsets.UTF_8))
    }
}
def requiredOrigin = { String key ->
    def url = new URI(System.getenv(key) ?: '')
    boolean secure = url.scheme == 'https'
    boolean legacyHttp = url.scheme == 'http' && System.getenv('NIFI_CAMERA_ALLOW_HTTP') == 'true'
    if (!(secure || legacyHttp) || !url.host || url.userInfo || url.query || url.fragment || !(url.path in ['', '/']))
        throw new IOException('Valid HTTPS origin required')
    url.toString().replaceFirst('/$', '')
}
try {
    String secret = System.getenv('NIFI_CAMERA_CLIENT_SECRET')
    if (!secret) throw new IOException('Missing camera credentials')
    def identityOrigin = requiredOrigin('NIFI_CAMERA_IDENTITY_ORIGIN')
    def gatewayOrigin = requiredOrigin('NIFI_CAMERA_GATEWAY_ORIGIN')
    def issue = post(identityOrigin + '/internal/v1/identity/service-tokens')
    String token
    try {
        issue.setRequestProperty('Content-Type', 'application/json')
        issue.outputStream.withCloseable { it.write(JsonOutput.toJson([
            clientId: System.getenv('NIFI_CAMERA_CLIENT_ID') ?: 'nifi-camera',
            clientSecret: secret, audience: 'aura-profile', scope: 'profile:camera'
        ]).getBytes(StandardCharsets.UTF_8)) }
        if (issue.responseCode != 200) throw new IOException('Camera token unavailable')
        def response = readJson(issue)
        if (response.tokenType != 'Bearer' || !(response.accessToken ==~ /[A-Za-z0-9_.-]{16,16384}/))
            throw new IOException('Invalid credential response')
        token = response.accessToken
    } finally { issue.disconnect() }

    def req = post(gatewayOrigin + '/api/v1/analyses/camera' +
        (ff.getAttribute('camera.clinic_id') ? '?clinicContextId=' + ff.getAttribute('camera.clinic_id') : ''))
    try {
        String eventId = ff.getAttribute('gateway.idempotency_key')
        String hash = ff.getAttribute('image.sha256')
        String device = ff.getAttribute('camera.device_id')
        req.setRequestProperty('Authorization', 'Bearer ' + token)
        req.setRequestProperty('Content-Type', ff.getAttribute('mime.type'))
        req.setRequestProperty('Idempotency-Key', eventId)
        req.setRequestProperty('X-Tenant-Id', ff.getAttribute('camera.tenant_id'))
        req.setRequestProperty('X-Device-Id', device)
        req.setRequestProperty('X-Camera-SHA256', hash)
        req.setRequestProperty('X-Camera-Version', '1')
        req.setFixedLengthStreamingMode(ff.size)
        req.outputStream.withCloseable { output ->
            session.read(ff, { input -> input.transferTo(output) } as InputStreamCallback)
        }
        int status = req.responseCode
        ff = session.putAttribute(ff, 'camera.delivery.http_status', Integer.toString(status))
        if (status == 202) {
            def receipt = readJson(req)
            if (receipt.requestId != eventId || receipt.deviceId != device || receipt.version != 1 ||
                receipt.imageSha256 != hash || receipt.imageCount != 1 || !(receipt.processingStatus in ['QUEUED', 'DUPLICATE']) ||
                !receipt.batchId || !receipt.analysisId || !receipt.sourceAcceptedAt)
                throw new IOException('Receipt did not match')
            ff = session.write(ff, { it.write(JsonOutput.toJson(receipt).getBytes(StandardCharsets.UTF_8)) } as OutputStreamCallback)
            ff = session.putAllAttributes(ff, ['mime.type':'application/json', 'filename':eventId + '.receipt.json',
                'camera.reporting.contract':'receipt-v1', 'camera.receipt.request_id': eventId,
                'camera.receipt.batch_id': receipt.batchId, 'camera.reporting.retries':'0'])
            def values = [eventId, device, hash, receipt.batchId, '1', receipt.sourceAcceptedAt, receipt.correlationId]
            def arguments = [:]
            values.eachWithIndex { value, index ->
                arguments['sql.args.' + (index + 1) + '.type'] = '12'
                arguments['sql.args.' + (index + 1) + '.value'] = value.toString()
            }
            ff = session.putAllAttributes(ff, arguments)
            session.transfer(ff, REL_SUCCESS)
        } else {
            String code = null
            if (status == 409 && req.errorStream != null)
                code = req.errorStream.withCloseable { stream ->
                    def bytes = stream.readNBytes(8193)
                    bytes.length <= 8192 ? new JsonSlurper().parseText(new String(bytes, StandardCharsets.UTF_8)).error?.code : null
                }
            boolean retry = status >= 500 || status in [401, 408, 429] || code == 'ERR_REQUEST_IN_PROGRESS'
            ff = session.putAttribute(ff, 'camera.delivery.retry', retry.toString())
            session.transfer(ff, REL_FAILURE)
        }
    } finally { req.disconnect() }
} catch (IOException | IllegalArgumentException | groovy.json.JsonException | ProcessException error) {
    // NiFi wraps I/O failures raised during a FlowFile stream callback.
    if (error instanceof ProcessException && !(error.cause instanceof IOException)) throw error
    ff = session.putAttribute(ff, 'camera.delivery.retry', 'true')
    session.transfer(ff, REL_FAILURE)
}
