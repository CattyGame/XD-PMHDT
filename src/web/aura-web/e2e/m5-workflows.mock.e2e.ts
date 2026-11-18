import { expect, test, type Page, type WebSocketRoute } from '@playwright/test'

const id = (n: number) => `10000000-0000-4000-8000-${String(n).padStart(12, '0')}`
const account = id(1), patient = id(2), clinic = id(3), analysis = id(4), file = id(5), order = id(6)
const uid = 'm5-synthetic-browser-fixture'
const encoded = (value: object) => Buffer.from(JSON.stringify(value)).toString('base64url')
const token = `${encoded({ alg: 'none' })}.${encoded({ sub: uid, iat: Math.floor(Date.now() / 1000),
  exp: Math.floor(Date.now() / 1000) + 3600, firebase: { sign_in_provider: 'password' } })}.fixture`
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=', 'base64')

// Hermetic UI contract checks: every Firebase/API response is synthetic and intercepted.
async function fixture(page: Page, roles = ['DOCTOR', 'PATIENT']) {
  const state = { reviewStatus: 'PENDING', reviewVersion: 1, reviewHttp: 201, posts: 0, paid: false, paymentPosts: 0 }
  await page.route('https://identitytoolkit.googleapis.com/**', route => {
    const headers = { 'access-control-allow-origin': '*', 'access-control-allow-headers': '*' }
    if (route.request().method() === 'OPTIONS') return route.fulfill({ status: 204, headers })
    return route.fulfill({ headers, json: new URL(route.request().url()).pathname.endsWith('accounts:lookup')
      ? { users: [{ localId: uid, email: 'm5@example.invalid', emailVerified: true }] }
      : { localId: uid, email: 'm5@example.invalid', idToken: token, refreshToken: 'fixture-refresh', expiresIn: '3600', registered: true } })
  })
  await page.route('https://securetoken.googleapis.com/**', route => route.abort())
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname
    if (path !== '/api/v1/billing/packages') expect(request.headers().authorization).toBe(`Bearer ${token}`)
    if (path.endsWith('/identity/bootstrap')) return route.fulfill({ json: { accountId: account, firebaseUid: uid,
      roles, accountStatus: 'ACTIVE', profileProvisioningStatus: 'READY' } })
    if (path.endsWith('/profiles/me')) return route.fulfill({ json: { accountId: account, patientId: patient,
      personalTenantId: id(10), displayName: 'Synthetic M5', profileVersion: 1 } })
    if (path === '/api/v1/clinics') return route.fulfill({ json: { items: [{ clinicId: clinic, clinicName: 'Synthetic clinic', role: 'CLINIC_ADMIN', status: 'ACTIVE' }] } })
    const detail = { analysisId: analysis, batchId: id(7), patientId: patient, processingStatus: 'COMPLETED',
      reviewStatus: state.reviewStatus, reviewVersion: state.reviewVersion, createdAt: '2026-11-01T09:00:00Z',
      image: { fileId: file, kind: 'original' }, result: { riskAvailable: false, riskLevel: null, isMock: true,
        annotations: [], disclaimer: 'Synthetic software test; no clinical result' } }
    if (path.startsWith('/api/v1/doctor/') || path.includes('/files/')) {
      expect(url.searchParams.get('clinicContextId')).toBe(clinic)
      if (path.endsWith('/worklist')) return route.fulfill({ json: { items: [detail] } })
      if (path.endsWith('/reviews')) {
        state.posts++
        if (state.reviewHttp === 0) return route.abort('internetdisconnected')
        if (state.reviewHttp !== 201) return route.fulfill({ status: state.reviewHttp, json: { error: { code: 'ERR_FORBIDDEN' } } })
        expect(request.postDataJSON().baseVersion).toBe(1)
        state.reviewStatus = 'REVIEWED'; state.reviewVersion = 2
        return route.fulfill({ status: 201, json: { reviewId: id(8), analysisId: analysis, version: 2, isApproved: true } })
      }
      if (path.endsWith('/access')) return route.fulfill({ json: {
        url: `/api/v1/analyses/${analysis}/files/${file}/content?clinicContextId=${clinic}`,
        expiresAt: new Date(Date.now() + 60_000).toISOString() } })
      if (path.endsWith('/content')) return route.fulfill({ contentType: 'image/png', body: png })
      return route.fulfill({ json: detail })
    }
    if (path === '/api/v1/billing/packages') return route.fulfill({ json: { data: [{ id: 1, name: 'Synthetic package', price: 1000, credits: 10 }],
      source: 'legacy', cacheHit: false, ttlSeconds: 60, databaseReadCount: 1 } })
    if (path.startsWith('/api/v1/billing/wallets/')) return route.fulfill({ json: { ownerType: path.split('/').at(-2),
      ownerId: path.split('/').at(-1), environment: 'TEST', balance: state.paid ? 10 : 0 } })
    if (path === '/api/v1/billing/orders' && request.method() === 'POST') {
      expect(request.postDataJSON().billingOwner).toEqual({ type: 'CLINIC', id: clinic })
      expect(request.headers()['idempotency-key']).toMatch(/^[0-9a-f-]{36}$/)
      return route.fulfill({ status: 201, json: { orderId: order, status: 'PENDING', amount: 1000, currency: 'VND', credits: 10,
        packageName: 'Synthetic package', paymentUrl: '', createdAt: new Date().toISOString() } })
    }
    if (path === `/api/v1/billing/orders/${order}`) return route.fulfill({ json: { id: order, billingOwnerType: 'CLINIC',
      billingOwnerId: clinic, packageNameSnapshot: 'Synthetic package', priceAmountSnapshot: 1000, currencyCodeSnapshot: 'VND',
      creditsSnapshot: 10, environment: 'TEST', status: state.paid ? 'COMPLETED' : 'PENDING' } })
    if (path === '/api/v1/billing/sandbox/pay') {
      state.paymentPosts++; state.paid = true
      return route.fulfill({ json: { orderId: order, transactionId: 'synthetic', status: 'COMPLETED', message: '', creditsAdded: 10, balanceAfter: 10, replayed: false } })
    }
    throw new Error(`Unexpected fixture API ${request.method()} ${path}`)
  })
  await page.goto('/login')
  await page.getByLabel('Email', { exact: true }).fill('m5@example.invalid')
  await page.getByLabel('Mật khẩu', { exact: true }).fill('synthetic-fixture-password')
  await page.getByRole('button', { name: 'Đăng nhập bằng Email' }).click()
  await expect(page).toHaveURL(/\/$/)
  return state
}

async function openDoctor(page: Page) {
  await page.goto('/doctor/workspace')
  await page.getByRole('textbox', { name: 'Clinic Context ID' }).fill(clinic)
  await page.getByRole('button', { name: 'Tải worklist' }).click()
  await page.getByRole('button', { name: 'Mở chi tiết' }).click()
  await expect(page.getByRole('heading', { name: 'Kết luận của bác sĩ' })).toBeVisible()
  await expect(page.getByRole('img', { name: 'Ảnh võng mạc gốc' }).first()).toHaveAttribute('src', /^blob:/)
}

test('MOCK: SCRUM-394 resends the same message, catches up through pages, and clears the thread on revoke', async ({ page }) => {
  await fixture(page, ['PATIENT'])
  await page.setViewportSize({ width: 390, height: 844 })
  const conversationId = id(30), peer = id(31), start = Date.UTC(2026, 10, 16)
  const knownMessage = (index: number, text: string, senderAccountId = peer) => ({
    messageId: id(100 + index), clientMessageId: id(200 + index), conversationId,
    senderAccountId, text, sentAt: new Date(start + index * 1000).toISOString(), deliveryState: 'PERSISTED',
  })
  const messages = [knownMessage(0, 'Tin đã lưu từ bác sĩ')]
  const postIds: string[] = []
  let unread = 1, revoked = false, joins = 0, nextPageReads = 0
  let socket: WebSocketRoute | undefined
  const frame = (value: object) => JSON.stringify(value) + '\u001e'
  function broadcast(target: string, argument: object) {
    socket?.send(frame({ type: 1, target, arguments: [argument] }))
  }
  await page.route(url => url.pathname === '/hubs/consultations/negotiate', route => {
    expect(route.request().headers().authorization).toBe(`Bearer ${token}`)
    return route.fulfill({ json: { negotiateVersion: 1, connectionId: 'fixture-connection',
      connectionToken: 'fixture-connection-token', availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }] } })
  })
  await page.routeWebSocket(url => url.pathname === '/hubs/consultations', ws => {
    socket = ws
    ws.onMessage(raw => {
      for (const part of String(raw).split('\u001e').filter(Boolean)) {
        const request = JSON.parse(part)
        if (request.protocol === 'json') ws.send(frame({}))
        if (request.target === 'JoinConversation') {
          expect(request.arguments).toEqual([{ type: 'JoinConversation', conversationId }])
          joins++
          ws.send(frame({ type: 3, invocationId: request.invocationId }))
        }
      }
    })
  })
  await page.route(url => url.pathname.startsWith('/api/v1/consultations'), async route => {
    const request = route.request(), url = new URL(request.url())
    expect(request.headers().authorization).toBe(`Bearer ${token}`)
    if (url.pathname === '/api/v1/consultations') return route.fulfill({ json: {
      items: revoked ? [] : [{ conversationId, analysisId: analysis, clinicContextId: clinic, status: 'OPEN', unreadCount: unread }],
      nextCursor: null,
    } })
    if (revoked) return route.fulfill({ status: 403, json: { error: { code: 'ERR_FORBIDDEN' } } })
    if (url.pathname.endsWith('/read')) {
      expect(request.postDataJSON().lastReadMessageId).toBe(id(100))
      unread = 0
      broadcast('ReadCursorAdvanced', { type: 'ReadCursorAdvanced', conversationId, accountId: account,
        lastReadMessageId: id(100), readAt: new Date(start + 1000).toISOString() })
      return route.fulfill({ json: { id: id(40), status: 'READ', version: 1 } })
    }
    if (request.method() === 'POST') {
      const body = request.postDataJSON()
      postIds.push(body.clientMessageId)
      if (postIds.length === 1) {
        messages.push({ ...knownMessage(1, body.text, account), clientMessageId: body.clientMessageId })
        return route.abort('failed')
      }
      expect(body.clientMessageId).toBe(postIds[0])
      const saved = messages[1]
      broadcast('MessagePersisted', { ...saved, type: 'MessagePersisted' })
      broadcast('MessagePersisted', { ...saved, type: 'MessagePersisted' })
      return route.fulfill({ json: saved })
    }
    const beforeId = url.searchParams.get('beforeId')
    if (beforeId) nextPageReads++
    const sorted = [...messages].reverse()
    const offset = beforeId ? sorted.findIndex(item => item.messageId === beforeId) + 1 : 0
    const rows = sorted.slice(offset, offset + 20)
    return route.fulfill({ json: { items: rows,
      nextBeforeId: offset + rows.length < sorted.length ? rows.at(-1)?.messageId : null } })
  })
  await page.goto('/consultations')
  await page.getByRole('button', { name: /Ca .*1 chưa đọc/ }).click()
  await expect(page.getByText('Đang nhận tin trực tiếp')).toBeVisible()
  await expect(page.getByText('Tin đã lưu từ bác sĩ')).toBeVisible()
  await page.getByRole('button', { name: 'Đánh dấu đã đọc' }).click()
  await expect(page.getByText('1 chưa đọc')).toHaveCount(0)
  const synthetic = '<img src=x onerror=alert(1)> Nội dung thử'
  const composer = page.getByRole('textbox', { name: 'Tin nhắn', exact: true })
  await composer.fill(synthetic)
  await page.getByRole('button', { name: 'Gửi tin nhắn' }).click()
  await expect(page.getByRole('button', { name: 'Gửi lại tin này' })).toBeEnabled()
  await page.getByRole('button', { name: 'Gửi lại tin này' }).click()
  await expect(page.getByText(synthetic, { exact: true })).toHaveCount(1)
  await expect(page.getByRole('list', { name: 'Tin nhắn đã lưu' }).locator('img')).toHaveCount(0)
  for (let index = 2; index <= 24; index++) messages.push(knownMessage(index, 'Tin phục hồi ' + index))
  await socket?.close({ code: 1011 })
  await expect.poll(() => joins).toBe(2)
  await expect(page.getByText('Tin phục hồi 2', { exact: true })).toBeAttached()
  await expect(page.getByText('Tin phục hồi 24', { exact: true })).toBeAttached()
  expect(nextPageReads).toBeGreaterThan(0)
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1)
  await composer.fill('Bản nháp riêng tư')
  await page.evaluate(() => window.dispatchEvent(new Event('offline')))
  await expect(page.getByRole('button', { name: 'Gửi tin nhắn' })).toBeDisabled()
  await expect(composer).toHaveValue('Bản nháp riêng tư')
  await page.evaluate(() => window.dispatchEvent(new Event('online')))
  await expect(page.getByRole('button', { name: 'Gửi tin nhắn' })).toBeEnabled()
  revoked = true
  broadcast('AccessRevoked', { type: 'AccessRevoked', conversationId, code: 'ACCESS_REVOKED' })
  await expect(page.getByText(/403 · Quyền truy cập/)).toBeVisible()
  await expect(page.getByRole('list', { name: 'Tin nhắn đã lưu' })).toHaveCount(0)
  await expect(composer).toHaveCount(0)
  expect(await page.evaluate(() => Object.values(localStorage).join(' '))).not.toContain('Bản nháp riêng tư')
})

test('MOCK: SCRUM-394 starts consultation using the eligible doctor list and reuses the create key', async ({ page }) => {
  await fixture(page, ['PATIENT'])
  const doctor = id(40), conversationId = id(41), keys: string[] = []
  await page.route(url => url.pathname === `/api/v1/analyses/${analysis}/consultation-doctors`, route => {
    expect(new URL(route.request().url()).searchParams.get('clinicContextId')).toBe(clinic)
    return route.fulfill({ json: { analysisId: analysis, items: [{ accountId: doctor, displayName: 'Bác sĩ được phân công' }] } })
  })
  await page.route(url => url.pathname === '/api/v1/consultations', route => {
    const request = route.request()
    if (request.method() === 'GET') return route.fulfill({ json: { items: [], nextCursor: null } })
    expect(request.postDataJSON()).toEqual({ doctorAccountId: doctor, analysisId: analysis, clinicContextId: clinic })
    const key = request.headers()['idempotency-key']
    expect(key).toMatch(/^[0-9a-f-]{36}$/)
    keys.push(key)
    if (keys.length === 1) return route.abort('failed')
    expect(key).toBe(keys[0])
    return route.fulfill({ status: 201, json: { conversationId, analysisId: analysis, clinicContextId: clinic,
      unreadCount: 0, status: 'OPEN' } })
  })
  await page.route(url => url.pathname === `/api/v1/consultations/${conversationId}/messages`,
    route => route.fulfill({ json: { items: [], nextBeforeId: null } }))
  await page.route(url => url.pathname === '/hubs/consultations/negotiate', route => route.fulfill({ status: 503 }))
  await page.goto(`/consultations?analysisId=${analysis}&clinicContextId=${clinic}`)
  await page.getByLabel('Bác sĩ tư vấn').selectOption(doctor)
  await page.getByRole('button', { name: 'Mở cuộc trò chuyện' }).click()
  await expect(page.getByRole('alert')).toBeVisible()
  await page.getByRole('button', { name: 'Mở cuộc trò chuyện' }).click()
  await expect(page.getByRole('region', { name: 'Nội dung tư vấn' })).toBeVisible()
  expect(keys).toHaveLength(2)
})

for (const width of [1024, 1366, 1920]) {
  test(`MOCK: Doctor approves through authenticated image bytes at ${width}px`, async ({ page }, info) => {
    const state = await fixture(page)
    await page.setViewportSize({ width, height: 900 })
    await openDoctor(page)
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1)
    await page.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }).click()
    await expect(page.getByText(/Ca chụp đã được đánh giá/)).toBeVisible()
    expect(state.posts).toBe(1)
    await page.screenshot({ path: info.outputPath(`doctor-approved-${width}.png`), fullPage: true })
  })
}

test('MOCK: offline review preserves draft; 409 blocks overwrite; revoked access clears detail', async ({ page }) => {
  const state = await fixture(page)
  await openDoctor(page)
  const notes = page.getByRole('textbox', { name: 'Ghi chú / khuyến nghị điều trị' })
  await notes.fill('Synthetic in-memory draft')
  state.reviewHttp = 0
  await page.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }).click()
  await expect(page.getByRole('alert').filter({ hasText: /Không/ })).toBeVisible()
  await expect(notes).toHaveValue('Synthetic in-memory draft')
  state.reviewHttp = 409
  await page.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }).click()
  await expect(page.getByRole('alertdialog')).toBeVisible()
  expect(state.posts).toBe(2)
  await page.getByRole('button', { name: 'Tải phiên bản mới nhất' }).click()
  await expect(page.getByRole('alertdialog')).toHaveCount(0)
  state.reviewHttp = 403
  await page.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }).click()
  await expect(page.getByRole('region', { name: 'Chi tiết ca chụp' })).toHaveCount(0)
  expect(await page.evaluate(() => Object.values(localStorage).some(value => value.includes('Synthetic in-memory draft')))).toBe(false)
})

test('MOCK: global PATIENT with ClinicAdmin membership buys for clinic; refresh reads current credit', async ({ page }) => {
  const state = await fixture(page, ['PATIENT'])
  await page.setViewportSize({ width: 1024, height: 900 })
  await page.goto('/billing/packages')
  await page.getByRole('radio', { name: 'Ví tổ chức' }).check()
  await expect(page.getByRole('status', { name: 'Thông tin Credit' }).getByText('0', { exact: true })).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1)
  await page.getByRole('button', { name: 'Mua gói', exact: true }).click()
  await page.getByRole('button', { name: 'Thanh toán thành công (Sandbox)' }).click()
  await expect(page.getByText('Thanh toán đã được backend xác nhận')).toBeVisible()
  await expect(page.getByRole('status', { name: 'Thông tin Credit' }).getByText('10', { exact: true })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('status', { name: 'Thông tin Credit' }).getByText('10', { exact: true })).toBeVisible()
  expect(state.paymentPosts).toBe(1)
})
