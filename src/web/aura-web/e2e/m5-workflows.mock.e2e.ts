import { expect, test, type Page } from '@playwright/test'

const id = (n: number) => `10000000-0000-4000-8000-${String(n).padStart(12, '0')}`
const account = id(1), patient = id(2), clinic = id(3), analysis = id(4), file = id(5), order = id(6)
const uid = 'm5-synthetic-browser-fixture'
const encoded = (value: object) => Buffer.from(JSON.stringify(value)).toString('base64url')
const token = `${encoded({ alg: 'none' })}.${encoded({ sub: uid, iat: Math.floor(Date.now() / 1000),
  exp: Math.floor(Date.now() / 1000) + 3600, firebase: { sign_in_provider: 'password' } })}.fixture`
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=', 'base64')

// Hermetic UI contract checks: every Firebase/API response is synthetic and intercepted.
async function fixture(page: Page, roles = ['DOCTOR', 'PATIENT'], research = false) {
  const state = { reviewStatus: 'PENDING', reviewVersion: 1, reviewHttp: 201, posts: 0, paid: false, paymentPosts: 0,
    feedbackStatus: 201, feedbackPosts: 0 }
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
      image: { fileId: file, kind: 'original' }, result: { riskAvailable: false, riskLevel: null, isMock: !research,
        annotations: research ? ['overlay', 'mask', 'annotated', 'heatmap'].map((kind, i) => ({ kind, fileId: id(21 + i) })) : [],
        disclaimer: 'Synthetic software test; no clinical result',
        ...(research ? { resultId: id(20), version: 3, modelVersion: 'frangi-fixture', configVersion: 'config-fixture',
          thresholdVersion: 'threshold-fixture', research: {
            avr: { measurement_available: false, avr: null, crae_px: null, crve_px: null, unavailable_reason: 'LOW_QUALITY',
              model_version: 'avr-fixture', model_sha256: 'a'.repeat(64) },
            dr: { probability_dr: 0.8, model_version: 'dr-fixture', checkpoint_sha256: 'b'.repeat(64),
              gradcam_method: 'GRAD_CAM', gradcam_geometry: 'IMAGENET_CENTER_CROP_224' },
          } } : {}) } }
    if (path === `/api/v1/analyses/${analysis}/feedback`) {
      expect(url.searchParams.get('clinicContextId')).toBe(clinic)
      expect(request.postDataJSON()).toEqual({ resultId: id(20), modelVersion: 'frangi-fixture',
        reviewVersion: state.reviewVersion, feedbackType: 'VESSEL_TYPE', comment: 'Synthetic feedback' })
      state.feedbackPosts++
      if (state.feedbackStatus !== 201) return route.fulfill({ status: state.feedbackStatus,
        json: { error: { code: state.feedbackStatus === 409 ? 'ERR_CONCURRENT_REVIEW' : 'ERR_FORBIDDEN' } } })
      return route.fulfill({ status: 201, json: { feedbackId: id(25), status: 'ACCEPTED', version: state.reviewVersion } })
    }
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
        url: path.replace(/access$/, 'content') + `?clinicContextId=${clinic}`,
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

test('MOCK: SCRUM-389 shows provenance, isolated crop and versioned feedback on mobile', async ({ page }, info) => {
  const state = await fixture(page, ['DOCTOR'], true)
  await page.setViewportSize({ width: 390, height: 844 })
  await openDoctor(page)
  await expect(page.getByText('Không đo được (LOW_QUALITY)')).toBeVisible()
  await page.getByLabel('Lớp ảnh').selectOption('annotated')
  await expect(page.getByRole('img', { name: 'A/V · động mạch và tĩnh mạch' })).toHaveAttribute('src', /^blob:/)
  await page.getByLabel('Lớp ảnh').selectOption('heatmap')
  await expect(page.getByRole('img', { name: 'Grad-CAM · ảnh cắt 224 × 224' })).toHaveAttribute('src', /^blob:/)
  await expect(page.getByLabel('Lớp phủ mạch máu')).toBeDisabled()
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1)
  await page.screenshot({ path: info.outputPath('doctor-research-mobile.png'), fullPage: true })
  async function feedback() {
    await page.getByLabel('Loại phản hồi').selectOption('VESSEL_TYPE')
    await page.getByLabel('Nhận xét về kết quả AI').fill('Synthetic feedback')
    await page.getByRole('button', { name: 'Gửi phản hồi AI' }).click()
  }
  await feedback()
  await expect(page.getByText('Đã lưu phản hồi cho phiên bản đang xem.')).toBeVisible()
  state.feedbackStatus = 409
  await feedback()
  await expect(page.getByRole('alertdialog')).toContainText('Phản hồi chưa được lưu')
  state.reviewVersion = 2
  await page.getByRole('button', { name: 'Tải phiên bản mới nhất' }).click()
  await expect(page.getByRole('alertdialog')).toHaveCount(0)
  state.feedbackStatus = 403
  await feedback()
  await expect(page.getByRole('region', { name: 'Chi tiết ca chụp' })).toHaveCount(0)
  expect(state.feedbackPosts).toBe(3)
})

async function openDoctor(page: Page) {
  await page.goto('/doctor/workspace')
  await page.getByRole('textbox', { name: 'Clinic Context ID' }).fill(clinic)
  await page.getByRole('button', { name: 'Tải worklist' }).click()
  await page.getByRole('button', { name: 'Mở chi tiết' }).click()
  await expect(page.getByRole('heading', { name: 'Kết luận của bác sĩ' })).toBeVisible()
  await expect(page.getByRole('img', { name: 'Ảnh võng mạc gốc' }).first()).toHaveAttribute('src', /^blob:/)
}

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
