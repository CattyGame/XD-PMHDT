import { expect, test } from '@playwright/test'

const accountId = '10000000-0000-4000-8000-000000000001'
const patientId = '20000000-0000-4000-8000-000000000002'
const batchId = '30000000-0000-4000-8000-000000000003'
const uid = 'aura-upload-fixture'
const email = 'upload-test@example.invalid'
const encode = (value: object) => Buffer.from(JSON.stringify(value)).toString('base64url')
const fixtureToken = `${encode({ alg: 'none' })}.${encode({
  sub: uid, iat: Math.floor(Date.now() / 1000), exp: Math.floor(Date.now() / 1000) + 3600,
  firebase: { sign_in_provider: 'password' },
})}.fixture`
const uploadPath = '/api/v1/analysis-upload-batches'
const inventoryPath = `${uploadPath}/${batchId}`

type ManifestItem = { itemId: string; fileName: string; patientId: string; eyeSide: string; uploadStatus: string }

test('MOCK: previews 100 dropped images and retries only the failed upload', async ({ page }) => {
  let items: ManifestItem[] = []
  let manifests = 0, finalizations = 0, active = 0, peak = 0
  let disconnected = false
  const requests: string[] = []
  const uploaded = new Set<string>()
  const inventory = () => ({ batchId, statusUrl: `/api/v1/batches/${batchId}`, createdAt: '2026-11-04T00:00:00Z',
    items, status: 'UPLOADING' })

  // Firebase and application calls stay inside this synthetic browser fixture.
  await page.route('https://identitytoolkit.googleapis.com/**', async route => {
    const path = new URL(route.request().url()).pathname
    const headers = { 'access-control-allow-origin': '*', 'access-control-allow-headers': '*' }
    if (route.request().method() === 'OPTIONS') return route.fulfill({ status: 204, headers })
    if (path.endsWith('accounts:signInWithPassword')) return route.fulfill({ headers, json: {
      localId: uid, email, idToken: fixtureToken, refreshToken: 'fixture-refresh', expiresIn: '3600', registered: true,
    } })
    if (path.endsWith('accounts:lookup')) return route.fulfill({ headers, json: {
      users: [{ localId: uid, email, emailVerified: true, providerUserInfo: [{ providerId: 'password', email }] }],
    } })
    return route.fulfill({ headers, json: {} })
  })
  await page.route('https://securetoken.googleapis.com/**', route => route.abort())
  await page.route('**/api/v1/**', async route => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    const method = request.method()
    expect(request.headers().authorization).toBe(`Bearer ${fixtureToken}`)
    if (path.endsWith('/identity/bootstrap')) return route.fulfill({ json: {
      accountId, firebaseUid: uid, accountStatus: 'ACTIVE', profileProvisioningStatus: 'READY', roles: ['PATIENT'],
    } })
    if (path.endsWith('/profiles/me')) return route.fulfill({ json: {
      accountId, patientId, personalTenantId: accountId, displayName: 'Hồ sơ tổng hợp', profileVersion: 1,
    } })
    if (path === uploadPath && method === 'POST') {
      manifests++
      expect(request.headers()['idempotency-key']).toBeTruthy()
      items = (request.postDataJSON().items as ManifestItem[]).map(item => ({ ...item, uploadStatus: 'DECLARED' }))
      expect(items).toHaveLength(100)
      expect(items.slice(0, 2).map(item => item.eyeSide)).toEqual(['right', 'left'])
      return route.fulfill({ status: 201, json: inventory() })
    }
    if (method === 'PUT' && path.startsWith(`${inventoryPath}/items/`)) {
      const item = items.find(value => value.itemId === path.split('/').at(-1))!
      requests.push(item.fileName)
      expect(request.headers()['idempotency-key']).toBe(item.itemId)
      const multipart = request.postDataBuffer()!.toString('latin1')
      expect(multipart.match(/name="image"/g)).toHaveLength(1)
      expect(multipart).toContain(`filename="${item.fileName}"`)
      expect(uploaded.has(item.itemId)).toBe(false)
      active++
      peak = Math.max(peak, active)
      try {
        await new Promise(resolve => setTimeout(resolve, 30))
        if (item.fileName === 'sample-0.png' && !disconnected) {
          disconnected = true
          await route.abort('connectionreset')
        } else {
          uploaded.add(item.itemId)
          item.uploadStatus = 'STAGED'
          await route.fulfill({ json: item })
        }
      } finally { active-- }
      return
    }
    if (path === inventoryPath && method === 'GET') {
      requests.push('GET inventory')
      return route.fulfill({ json: inventory() })
    }
    if (path === `${inventoryPath}/finalize` && method === 'POST') {
      finalizations++
      expect(uploaded.size).toBe(100)
      return route.fulfill({ status: 202, json: { ...inventory(),
        items: items.map(item => ({ analysisId: item.itemId, fileName: item.fileName, processingStatus: 'QUEUED' })),
      } })
    }
    return route.fulfill({ status: 404, json: { error: { code: 'ERR_NOT_FOUND' } } })
  })

  await page.goto('/login')
  await page.getByLabel('Email', { exact: true }).fill(email)
  await page.getByLabel('Mật khẩu', { exact: true }).fill('fixture-only')
  await page.getByRole('button', { name: 'Đăng nhập bằng Email' }).click()
  await expect(page).toHaveURL(/\/$/)
  await page.goto('/upload')

  await page.getByLabel('Chọn ảnh võng mạc').evaluate(async input => {
    const transfer = new DataTransfer()
    const canvas = document.createElement('canvas')
    canvas.width = canvas.height = 256
    const context = canvas.getContext('2d')!
    for (let index = 0; index < 100; index++) {
      context.fillStyle = `rgb(${index}, 100, 200)`
      context.fillRect(0, 0, canvas.width, canvas.height)
      const blob = await new Promise<Blob>(resolve => canvas.toBlob(value => resolve(value!), 'image/png'))
      transfer.items.add(new File([blob], `sample-${index}.png`, { type: 'image/png' }))
    }
    input.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer }))
  })
  await expect(page.getByRole('img', { name: /^Xem trước sample-/ })).toHaveCount(100)
  await expect(page.getByRole('img', { name: 'Xem trước sample-0.png' })).toHaveJSProperty('naturalWidth', 256)
  await page.getByLabel('Mắt của sample-0.png', { exact: true }).selectOption('right')
  await page.getByRole('button', { name: 'Gửi ảnh phân tích' }).click()
  await expect(page.getByRole('progressbar', { name: 'Tiến độ lưu ảnh lên máy chủ' })).toHaveAttribute('value', '99', { timeout: 15_000 })
  const retry = page.getByRole('button', { name: 'Thử lại sample-0.png' })
  await expect(retry).toBeEnabled()
  expect(finalizations).toBe(0)
  const beforeRetry = requests.length
  await retry.click()
  await expect(page.getByRole('heading', { name: 'Thông tin tiếp nhận' })).toBeVisible()
  expect(requests.slice(beforeRetry)).toEqual(['GET inventory', 'sample-0.png', 'GET inventory'])
  expect([manifests, finalizations, uploaded.size]).toEqual([1, 1, 100])
  expect(peak).toBe(4)
  expect(await page.evaluate(() => [...Object.values(localStorage), ...Object.values(sessionStorage)]
    .some(value => /sample-\d+\.png|Hồ sơ tổng hợp/.test(String(value))))).toBe(false)
})
