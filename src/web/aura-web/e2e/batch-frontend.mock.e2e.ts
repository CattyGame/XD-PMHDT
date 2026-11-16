import { expect, test, type Page } from '@playwright/test'

const accountId = '10000000-0000-4000-8000-000000000001'
const patientId = '20000000-0000-4000-8000-000000000002'
const batchId = '30000000-0000-4000-8000-000000000003'
const reportId = '40000000-0000-4000-8000-000000000004'
const uid = 'aura-playwright-mock-user'
const email = 'batch-test@example.invalid'
const encoded = (data: object) => Buffer.from(JSON.stringify(data)).toString('base64url')
const mockToken = `${encoded({ alg: 'none' })}.${encoded({
  sub: uid, iat: Math.floor(Date.now() / 1000), exp: Math.floor(Date.now() / 1000) + 3600,
  firebase: { sign_in_provider: 'password' },
})}.mock`
const uploadPath = '/api/v1/analysis-upload-batches'
const statusUrl = `/api/v1/batches/${batchId}`
const contentPath = `/api/v1/reports/${reportId}/content`
const createdAt = '2026-11-04T00:00:00Z'

type ManifestItem = { itemId: string; fileName: string; patientId: string; eyeSide: string; uploadStatus: string }

async function storageAudit(page: Page) {
  return page.evaluate(async ({ patient, fixtureEmail }) => {
    const medicalData = (value: string) => [patient, 'Fixture tổng hợp', 'data:image/', 'sample-0.png']
      .some(marker => value.includes(marker))
    const authData = (value: string) => [fixtureEmail, 'mock-refresh', 'firebase:authUser:', 'stsTokenManager']
      .some(marker => value.includes(marker))
    const values = (storage: Storage) => Object.entries(storage).flat().join('\n')
    const indexed: unknown[] = []
    for (const { name } of await indexedDB.databases()) {
      if (!name) continue
      const database = await new Promise<IDBDatabase>((resolve, reject) => {
        const opening = indexedDB.open(name)
        opening.onsuccess = () => resolve(opening.result)
        opening.onerror = () => reject(opening.error)
      })
      try {
        for (const store of Array.from(database.objectStoreNames)) {
          const records = await new Promise<unknown[]>((resolve, reject) => {
            const request = database.transaction(store).objectStore(store).getAll()
            request.onsuccess = () => resolve(request.result)
            request.onerror = () => reject(request.error)
          })
          indexed.push(...records)
        }
      } finally { database.close() }
    }
    const cachedUrls = (await Promise.all((await caches.keys()).map(async name => (await caches.open(name)).keys())))
      .flat().map(request => new URL(request.url).pathname)
    const permanent = values(localStorage) + JSON.stringify(indexed)
    const session = values(sessionStorage)
    return {
      medical: medicalData(permanent + session),
      persistentAuth: authData(permanent),
      sessionAuth: authData(session),
      cachedApi: cachedUrls.some(path => path.startsWith('/api/')),
    }
  }, { patient: patientId, fixtureEmail: email })
}

async function verifyStorage(page: Page, logs: string[]) {
  const signedIn = { medical: false, persistentAuth: false, sessionAuth: true, cachedApi: false }
  expect(await storageAudit(page)).toEqual(signedIn)

  // Recreate the legacy Firebase storage layout using only the synthetic browser session.
  const moved = await page.evaluate(() => {
    localStorage.setItem('unrelated-feature', 'keep')
    sessionStorage.setItem('unrelated-tab-feature', 'keep')
    const entry = Object.entries(sessionStorage).find(([key]) => key.startsWith('firebase:authUser:'))
    if (!entry) return false
    localStorage.setItem(entry[0], entry[1])
    sessionStorage.removeItem(entry[0])
    return true
  })
  expect(moved).toBe(true)
  await page.reload()
  await expect(page.getByRole('progressbar', { name: 'Tiến độ xử lý batch' })).toHaveAttribute('value', '100')
  await expect.poll(() => storageAudit(page)).toEqual(signedIn)

  await page.goto('/')
  await page.getByRole('button', { name: 'Đăng xuất', exact: true }).click()
  await expect(page).toHaveURL(/\/login$/)
  expect(await storageAudit(page)).toEqual({ ...signedIn, sessionAuth: false })
  expect(await page.evaluate(() => [
    localStorage.getItem('unrelated-feature'), sessionStorage.getItem('unrelated-tab-feature'),
  ])).toEqual(['keep', 'keep'])
  expect(logs.some(line => [patientId, email, 'Fixture tổng hợp', mockToken, 'mock-refresh', 'sample-0.png']
    .some(marker => line.includes(marker)))).toBe(false)
}

test('MOCK: 100 uploads survive offline and a lost acknowledgement, then refresh without resubmission', async ({ page, context }) => {
  test.setTimeout(60_000)
  let items: ManifestItem[] = []
  let manifests = 0, finalizations = 0, statusReads = 0, active = 0, peak = 0
  let completed = 0
  let offline = false
  let lostItem: ManifestItem | undefined
  const puts: string[] = []
  const events: string[] = []
  const logs: string[] = []
  page.on('console', message => logs.push(message.text()))
  const uploaded = new Set<string>()
  const requestedReports: string[] = []
  const inventory = () => ({ batchId, statusUrl, createdAt, items, status: 'UPLOADING' })
  const receiptItems = () => items.map(item => ({ analysisId: item.itemId, fileName: item.fileName, processingStatus: 'QUEUED' }))

  // Firebase REST and all project APIs are intercepted. No external account, upload or credit action is used.
  await page.route('https://identitytoolkit.googleapis.com/**', async route => {
    const path = new URL(route.request().url()).pathname
    const headers = { 'access-control-allow-origin': '*', 'access-control-allow-headers': '*' }
    if (route.request().method() === 'OPTIONS') return route.fulfill({ status: 204, headers })
    if (path.endsWith('accounts:signInWithPassword')) return route.fulfill({ headers, json: {
      localId: uid, email, idToken: mockToken, refreshToken: 'mock-refresh', expiresIn: '3600', registered: true,
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
    expect(request.headers().authorization).toBe(`Bearer ${mockToken}`)
    if (path.endsWith('/identity/bootstrap')) return route.fulfill({ json: {
      accountId, firebaseUid: uid, accountStatus: 'ACTIVE', profileProvisioningStatus: 'READY', roles: ['PATIENT'],
    } })
    if (path.endsWith('/profiles/me')) return route.fulfill({ json: {
      accountId, patientId, personalTenantId: accountId, displayName: 'Fixture tổng hợp', profileVersion: 1,
    } })
    if (path === uploadPath && method === 'POST') {
      manifests++
      expect(request.headers()['idempotency-key']).toBeTruthy()
      items = (request.postDataJSON().items as ManifestItem[]).map(item => ({ ...item, uploadStatus: 'DECLARED' }))
      expect(items).toHaveLength(100)
      expect(items[0].eyeSide).toBe('right')
      expect(items[1].eyeSide).toBe('left')
      return route.fulfill({ status: 201, json: inventory() })
    }
    if (method === 'PUT' && path.startsWith(`${uploadPath}/${batchId}/items/`)) {
      const item = items.find(value => value.itemId === path.split('/').at(-1))!
      puts.push(item.itemId)
      events.push(`PUT ${item.itemId}`)
      expect(request.headers()['idempotency-key']).toBe(item.itemId)
      const multipart = request.postDataBuffer()!.toString('latin1')
      expect(multipart.match(/name="image"/g)).toHaveLength(1)
      expect(multipart).toContain(`filename="${item.fileName}"`)
      expect(uploaded.has(item.itemId)).toBe(false)
      if (offline) return route.abort('internetdisconnected')
      active++
      peak = Math.max(peak, active)
      try {
        uploaded.add(item.itemId)
        item.uploadStatus = 'STAGED'
        if (uploaded.size === 5 && !lostItem) {
          lostItem = item
          offline = true
          await context.setOffline(true)
          return await route.abort('internetdisconnected')
        }
        await route.fulfill({ json: item })
      } finally { active-- }
      return
    }
    if (path === `${uploadPath}/${batchId}` && method === 'GET') {
      events.push('GET inventory')
      if (offline) return route.abort('internetdisconnected')
      return route.fulfill({ json: inventory() })
    }
    if (path === `${uploadPath}/${batchId}/finalize` && method === 'POST') {
      finalizations++
      expect(uploaded.size).toBe(100)
      return route.fulfill({ status: 202, json: { batchId, statusUrl, createdAt, items: receiptItems() } })
    }
    if (path === statusUrl && method === 'GET') {
      statusReads++
      return route.fulfill({ json: { batchId, createdAt, batchStatus: completed === 100 ? 'COMPLETED' : 'IN_PROGRESS',
        summary: { total: 100, active: 100 - completed, succeeded: completed, failed: 0, duplicate: 0 },
        items: receiptItems().map((item, index) => ({ ...item, processingStatus: index < completed ? 'COMPLETED' : 'PROCESSING',
          reviewStatus: 'PENDING', riskLevel: null })),
      } })
    }
    if (path === `/api/v1/analyses/${items[1]?.itemId}/reports` && method === 'POST') {
      requestedReports.push(path)
      expect(request.postDataJSON()).toEqual({ format: 'pdf' })
      return route.fulfill({ status: 201, json: { reportId, format: 'pdf', downloadUrl: contentPath,
        expiresAt: new Date(Date.now() + 60_000).toISOString() } })
    }
    if (path === contentPath && method === 'GET') {
      return route.fulfill({ contentType: 'application/pdf', body: '%PDF-1.7 synthetic Playwright fixture' })
    }
    return route.fulfill({ status: 404, json: { error: { code: 'ERR_NOT_FOUND' } } })
  })

  await page.goto('/login')
  await page.getByLabel('Email', { exact: true }).fill(email)
  await page.getByLabel('Mật khẩu', { exact: true }).fill('playwright-fixture-only')
  await page.getByRole('button', { name: 'Đăng nhập bằng Email' }).click()
  await expect(page).toHaveURL(/\/$/)
  await page.goto('/upload')

  const images = page.getByLabel('Chọn ảnh võng mạc')
  await images.evaluate(async input => {
    const transfer = new DataTransfer()
    const canvas = document.createElement('canvas')
    canvas.width = canvas.height = 8
    const context = canvas.getContext('2d')!
    for (let index = 0; index < 100; index++) {
      context.fillStyle = `rgb(${index}, 100, 200)`
      context.fillRect(0, 0, 8, 8)
      const blob = await new Promise<Blob>(resolve => canvas.toBlob(value => resolve(value!), 'image/png'))
      transfer.items.add(new File([blob], `sample-${index}.png`, { type: 'image/png' }))
    }
    input.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer }))
  })
  await expect(page.getByRole('img', { name: /^Xem trước sample-/ })).toHaveCount(100)
  await expect(page.getByRole('img', { name: 'Xem trước sample-0.png' })).toHaveJSProperty('naturalWidth', 8)
  expect((await storageAudit(page)).medical).toBe(false)
  await page.getByLabel('Mắt của sample-0.png', { exact: true }).selectOption('right')
  await page.getByRole('button', { name: 'Gửi ảnh phân tích' }).evaluate(button => {
    const submit = button as HTMLButtonElement
    submit.click()
    submit.click()
  })
  const retryButtons = page.getByRole('button', { name: /^Thử lại sample-/ })
  await expect(retryButtons.first()).toBeEnabled({ timeout: 15_000 })
  expect([manifests, finalizations, uploaded.size, offline]).toEqual([1, 0, 5, true])
  expect(puts.length).toBeLessThanOrEqual(8)
  expect(lostItem).toBeDefined()
  await expect(page.getByRole('progressbar', { name: 'Tiến độ lưu ảnh lên máy chủ' })).toHaveAttribute('value', '4')

  await context.setOffline(false)
  offline = false
  // Select one failed file other than the one already staged on the server.
  const retry = retryButtons.filter({ hasNotText: `Thử lại ${lostItem!.fileName}` }).first()
  const retryName = (await retry.innerText()).trim().replace(/^Thử lại /, '')
  const retryId = items.find(item => item.fileName === retryName)!.itemId
  const retryStart = events.length
  await retry.click()
  await expect(page.getByRole('progressbar', { name: 'Tiến độ lưu ảnh lên máy chủ' })).toHaveAttribute('value', '6')
  const resume = page.getByRole('button', { name: 'Thử lại cùng lượt gửi' })
  await expect(resume).toBeEnabled()
  expect(events.slice(retryStart)).toEqual(['GET inventory', `PUT ${retryId}`, 'GET inventory'])
  expect(puts.filter(item => item === lostItem!.itemId)).toHaveLength(1)
  await resume.click()
  await page.getByRole('link', { name: /Theo dõi tiến trình batch/ }).click()
  await expect(page).toHaveURL(new RegExp(`/batches/${batchId}$`))
  const progress = page.getByRole('progressbar', { name: 'Tiến độ xử lý batch' })
  await expect(progress).toHaveAttribute('value', '0')
  completed = 68
  await expect(progress).toHaveAttribute('value', '68')
  const readsBeforeReload = statusReads
  const putsBeforeReload = puts.length
  await page.reload()
  await expect(page).toHaveURL(new RegExp(`/batches/${batchId}$`))
  await expect(progress).toHaveAttribute('value', '68')
  expect(statusReads).toBeGreaterThan(readsBeforeReload)
  await expect(page.getByRole('button', { name: /Tải tất cả/ })).toHaveCount(0)
  completed = 100
  await expect(progress).toHaveAttribute('value', '100', { timeout: 10_000 })
  await expect(page.getByRole('button', { name: 'Tải tất cả (ZIP)' })).toBeVisible()

  const download = page.waitForEvent('download')
  const row = page.getByRole('row').filter({ has: page.getByText('sample-1.png', { exact: true }) })
  await row.getByRole('button', { name: 'Tải báo cáo PDF' }).click()
  expect((await download).suggestedFilename()).toBe(`aura-${reportId}.pdf`)
  expect(requestedReports).toEqual([`/api/v1/analyses/${items[1].itemId}/reports`])
  expect([manifests, finalizations, uploaded.size]).toEqual([1, 1, 100])
  expect(puts).toHaveLength(putsBeforeReload)
  expect(peak).toBeLessThanOrEqual(4)
  await verifyStorage(page, logs)
})
