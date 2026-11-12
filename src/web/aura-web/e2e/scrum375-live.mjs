import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { pathToFileURL } from 'node:url'
import { parseArgs } from 'node:util'
import { inflateRawSync } from 'node:zlib'
import { chromium, expect } from '@playwright/test'

const uuid = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i
const mime = { pdf: 'application/pdf', csv: 'text/csv', zip: 'application/zip' }
const maxEntrySize = 4 * 1024 * 1024

function verifyPdf(bytes) {
  const text = bytes.toString('latin1')
  assert(text.startsWith('%PDF-') && text.includes('startxref') && text.trimEnd().endsWith('%%EOF'),
    'PDF signature, cross-reference or trailer missing')
}

function verifyCsv(bytes) {
  assert(bytes.toString('utf8').replace(/^\ufeff/, '').startsWith('recordType,caseCode,'),
    'CSV header does not match the batch export')
}

function archiveEntry(bytes, requestedName) {
  const end = bytes.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]))
  assert(end >= 0, 'ZIP central directory missing')
  let offset = bytes.readUInt32LE(end + 16)
  for (let remaining = bytes.readUInt16LE(end + 10); remaining > 0; remaining--) {
    assert(bytes.readUInt32LE(offset) === 0x02014b50, 'Invalid ZIP central directory')
    const nameLength = bytes.readUInt16LE(offset + 28)
    const name = bytes.toString('utf8', offset + 46, offset + 46 + nameLength)
    if (name === requestedName) {
      const method = bytes.readUInt16LE(offset + 10)
      const length = bytes.readUInt32LE(offset + 20)
      const expectedSize = bytes.readUInt32LE(offset + 24)
      const local = bytes.readUInt32LE(offset + 42)
      assert(expectedSize <= maxEntrySize && bytes.readUInt32LE(local) === 0x04034b50, 'Invalid ZIP entry')
      const start = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28)
      const packed = bytes.subarray(start, start + length)
      assert(method === 0 || method === 8, 'Unsupported ZIP compression')
      const content = method === 0 ? packed : inflateRawSync(packed, { maxOutputLength: maxEntrySize })
      assert(content.length === expectedSize, 'ZIP entry size mismatch')
      return content
    }
    offset += 46 + nameLength + bytes.readUInt16LE(offset + 30) + bytes.readUInt16LE(offset + 32)
  }
  throw new Error('A required ZIP entry was missing')
}

export function verifyArchive(bytes, batchId, analysisId) {
  const manifest = JSON.parse(archiveEntry(bytes, 'manifest.json').toString('utf8'))
  assert.equal(manifest.batchId, batchId)
  const entry = manifest.items.find(item => item.analysisId === analysisId && item.processingStatus === 'COMPLETED')
  assert.equal(entry?.pdfEntry, `case-${analysisId}.pdf`)
  const pdf = archiveEntry(bytes, entry.pdfEntry)
  verifyPdf(pdf)
  assert.equal(createHash('sha256').update(pdf).digest('hex'), entry.pdfSha256)
  verifyCsv(archiveEntry(bytes, 'summary.csv'))
}

async function verifyDownload(page, button, route, format) {
  const timeout = 130_000
  const create = page.waitForResponse(response => new URL(response.url()).pathname === route &&
    response.request().method() === 'POST' && response.request().postDataJSON()?.format === format,
  { timeout }).then(async response => {
    if (response.status() !== 201) throw new ReportHttpError(format, response.status())
    const { reportId } = await response.json()
    assert(uuid.test(reportId), 'Gateway did not return a valid report ID')
    return reportId
  })
  const content = page.waitForResponse(async response => {
    const path = new URL(response.url()).pathname
    return response.request().method() === 'GET' && path.startsWith('/api/v1/reports/') &&
      path === `/api/v1/reports/${await create}/content`
  }, { timeout }).then(async response => {
    if (response.status() !== 200) throw new ReportHttpError(format, response.status(), 'download')
    assert.equal((await response.headerValue('content-type'))?.split(';')[0], mime[format])
  })
  const browserDownload = page.waitForEvent('download', {
    timeout,
    predicate: async download => download.suggestedFilename() === `aura-${await create}.${format}`,
  })
  const [, , download] = await Promise.all([create, content, browserDownload, button.click()])
  return readFile(await download.path())
}

class ReportHttpError extends Error {
  constructor(format, status, action = 'creation') {
    super(`${format.toUpperCase()} report ${action} returned HTTP ${status}.`)
  }
}

export async function verifyBatch(page, batchId, clinicId) {
  assert(uuid.test(batchId) && (!clinicId || uuid.test(clinicId)), 'A synthetic batch UUID is required')
  const query = clinicId ? `?clinicContextId=${clinicId}` : ''
  const origin = new URL(page.url()).origin
  const path = `/api/v1/batches/${batchId}`
  const pending = new Set()
  let peak = 0
  const statusRequest = request => request.method() === 'GET' && new URL(request.url()).pathname === path
  const start = request => {
    if (statusRequest(request)) { pending.add(request); peak = Math.max(peak, pending.size) }
  }
  const finish = request => pending.delete(request)
  page.on('request', start)
  page.on('requestfinished', finish)
  page.on('requestfailed', finish)
  const readStatus = () => page.waitForResponse(response => statusRequest(response.request()), { timeout: 20_000 })
  try {
    const [first] = await Promise.all([readStatus(), page.goto(`${origin}/batches/${batchId}${query}`)])
    assert.equal(first.status(), 200)
    assert.equal((await first.json()).batchId, batchId)
    const [afterReload] = await Promise.all([readStatus(), page.reload()])
    assert.equal(afterReload.status(), 200)
    const progress = await afterReload.json()
    assert.equal(progress.batchId, batchId)
    const total = progress.totalDeclared ?? progress.summary.total
    const finished = progress.summary.succeeded + progress.summary.failed + progress.summary.duplicate + (progress.admissionRejected ?? 0)
    await expect(page.getByRole('progressbar', { name: 'Tiến độ xử lý batch' })).toHaveAttribute(
      'value', String(total ? Math.round(finished * 100 / total) : 0),
    )
    assert.equal(peak, 1, 'Overlapping GETs for the same batch')
    console.log(`Batch GET and refresh HTTP 200, progress ${finished}/${total}, at most one GET in flight.`)
    if (progress.batchStatus === 'IN_PROGRESS') {
      await expect(page.getByRole('button', { name: 'Tải tất cả (ZIP)' })).toBeVisible({ timeout: 120_000 })
    }
    const row = page.getByRole('row').filter({ has: page.getByRole('button', { name: 'Tải báo cáo PDF' }) }).first()
    await row.waitFor()
    const href = await row.getByRole('link', { name: 'Xem kết quả chi tiết' }).getAttribute('href')
    const analysisId = new URL(href, origin).pathname.split('/').at(-1)
    assert(uuid.test(analysisId))
    const checks = [
      { name: 'Case PDF', button: row.getByRole('button', { name: 'Tải báo cáo PDF' }),
        route: `/api/v1/analyses/${analysisId}/reports`, format: 'pdf', verify: verifyPdf },
      { name: 'Batch CSV', button: page.getByRole('button', { name: 'Tải CSV' }),
        route: `${path}/reports`, format: 'csv', verify: verifyCsv },
      { name: 'Batch ZIP', button: page.getByRole('button', { name: 'Tải tất cả (ZIP)' }),
        route: `${path}/reports`, format: 'zip', verify: bytes => verifyArchive(bytes, batchId, analysisId) },
    ]
    let passed = true
    for (const { name, button, route, format, verify } of checks) {
      try {
        verify(await verifyDownload(page, button, route, format))
        console.log(`${name} passed format and resource binding checks.`)
      } catch (error) {
        passed = false
        console.error(`${name} failed. ${error instanceof ReportHttpError ? error.message : 'Check download and file validation.'}`)
      }
    }
    assert.equal(peak, 1)
    assert(passed, 'One or more report checks failed')
  } finally {
    page.off('request', start)
    page.off('requestfinished', finish)
    page.off('requestfailed', finish)
  }
}

async function main() {
  const { values } = parseArgs({ options: {
    email: { type: 'string' }, batch: { type: 'string' }, clinic: { type: 'string' },
  } })
  assert(values.email && values.batch && uuid.test(values.batch), 'Specify --email and --batch for your synthetic TEST account.')
  const browser = await chromium.launch({ headless: false })
  const page = await browser.newPage()
  try {
    await page.goto('http://127.0.0.1:5173/login')
    console.log('Sign in to the local AURA page manually with the TEST account.')
    await page.waitForURL('http://127.0.0.1:5173/', { timeout: 300_000 })
    const proof = await page.evaluate(async email => {
      const { getFirebaseAuth } = await import(new URL('/src/firebase/config.ts', location.origin).href)
      const auth = getFirebaseAuth()
      await auth?.authStateReady()
      if (auth?.currentUser?.email?.toLowerCase() !== email.toLowerCase()) return null
      const token = await auth.currentUser.getIdToken()
      const response = await fetch('/api/v1/identity/me', {
        headers: { Authorization: `Bearer ${token}` }, redirect: 'error', cache: 'no-store',
      })
      const profile = response.ok ? await response.json() : {}
      return { status: response.status, project: auth.app.options.projectId, roles: profile.roles }
    }, values.email)
    assert.equal(proof?.status, 200, 'The Gateway did not recognize the TEST session')
    console.log('Firebase/Gateway accepted the TEST session.', proof)
    await verifyBatch(page, values.batch, values.clinic)
  } finally { await browser.close() }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => {
    if (error instanceof ReportHttpError) console.error(error.message)
    else console.error(error.name === 'TimeoutError' ? 'Timed out waiting for login, batch completion or a report.' :
      'SCRUM-375 live check failed. Check the TEST account, batch state and local Gateway.')
    process.exitCode = 1
  })
}
