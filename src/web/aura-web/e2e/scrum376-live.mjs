import assert from 'node:assert/strict'
import { parseArgs } from 'node:util'
import { chromium, expect } from '@playwright/test'

const { values } = parseArgs({ options: {
  batch: { type: 'string' },
  origin: { type: 'string', default: 'http://127.0.0.1:5173' },
} })
const origin = new URL(values.origin)
assert(origin.protocol === 'http:' && ['127.0.0.1', 'localhost'].includes(origin.hostname),
  'Use a local AURA frontend.')
const batchId = values.batch
assert(batchId && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(batchId),
  'Pass --batch with the ID of a synthetic TEST batch that is still processing.')

const gateway = await fetch('http://127.0.0.1:5000/health', { signal: AbortSignal.timeout(5_000) })
assert.equal(gateway.status, 200, 'The local Gateway is not healthy.')
const browser = await chromium.launch({ headless: false })
try {
  const page = await browser.newPage()
  await page.goto(new URL('/login', origin).href)
  console.log('Đăng nhập tài khoản Firebase TEST trực tiếp trong Chromium.')
  await page.waitForURL(new URL('/', origin).href, { timeout: 300_000 })

  const statusPath = `/api/v1/batches/${batchId}`
  const batchPage = new URL(`/batches/${batchId}`, origin).href
  const writes = []
  page.on('request', request => {
    const path = new URL(request.url()).pathname
    if (request.method() !== 'GET' && /^\/api\/v1\/(analysis-upload-batches|analyses|batches)(?:\/|$)/.test(path)) {
      writes.push(request.method())
    }
  })
  const readStatus = () => page.waitForResponse(response => {
    const url = new URL(response.url())
    return url.origin === origin.origin && url.pathname === statusPath && response.request().method() === 'GET'
  }, { timeout: 20_000 })
  const progress = page.getByRole('progressbar', { name: 'Tiến độ xử lý batch' })
  async function verify(response) {
    assert.equal(response.status(), 200)
    assert.match(await response.headerValue('cache-control') ?? '', /\bno-store\b/i)
    const data = await response.json()
    assert.equal(data.batchId, batchId)
    const { total, succeeded, failed, duplicate } = data.summary
    const declared = data.totalDeclared ?? total
    const done = succeeded + failed + duplicate + (data.admissionRejected ?? 0)
    await expect(progress).toHaveAttribute('value', String(declared ? Math.round(done / declared * 100) : 0))
    console.log(`GET ${statusPath} HTTP 200 · ${done}/${declared}`)
    return data.batchStatus
  }

  const [initial] = await Promise.all([readStatus(), page.goto(batchPage)])
  assert.equal(await verify(initial), 'IN_PROGRESS', 'Start with a synthetic batch that is still processing.')
  const [restored] = await Promise.all([readStatus(), page.reload()])
  await expect(page).toHaveURL(batchPage)
  await verify(restored)
  await expect(page.getByText(/đã dừng polling/)).toBeVisible({ timeout: 180_000 })
  assert.equal(writes.length, 0, 'Refreshing the batch triggered an unexpected write.')
  console.log('F5 và polling đến trạng thái cuối đạt. Không tạo batch hoặc gửi lại ảnh.')
} finally {
  await browser.close()
}
