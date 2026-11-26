import { expect, test, type Page, type WebSocketRoute } from '@playwright/test'

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
    if (['/api/v1/admin/clinics', '/api/v1/admin/accounts', '/api/v1/admin/audit-events'].includes(path)) {
      return route.fulfill({ json: { items: [], nextCursor: null } })
    }
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
    if (path === '/api/v1/billing/packages') return route.fulfill({ json: { data: [{ id: 1, name: 'Synthetic package', price: 1000, credits: 10,
      priceVersionId: id(11), currency: 'VND', version: 1 }],
      source: 'legacy', cacheHit: false, ttlSeconds: 60, databaseReadCount: 1 } })
    if (path.startsWith('/api/v1/billing/wallets/')) return route.fulfill({ json: { ownerType: path.split('/').at(-2),
      ownerId: path.split('/').at(-1), environment: 'TEST', balance: state.paid ? 10 : 0 } })
    if (path === '/api/v1/billing/orders' && request.method() === 'POST') {
      expect(request.postDataJSON().billingOwner).toEqual({ type: 'CLINIC', id: clinic })
      expect(request.postDataJSON().packagePriceVersionId).toBe(id(11))
      expect(request.headers()['idempotency-key']).toMatch(/^[0-9a-f-]{36}$/)
      return route.fulfill({ status: 201, json: { orderId: order, status: 'PENDING', amount: 1000, currency: 'VND', credits: 10,
        packageName: 'Synthetic package', priceVersionId: id(11), paymentUrl: '', createdAt: new Date().toISOString() } })
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

test('MOCK: SCRUM-393 admin catalog binds the price version and reloads after conflict on mobile', async ({ page }) => {
  await fixture(page, ['SYSTEM_ADMIN'])
  await page.setViewportSize({ width: 390, height: 844 })
  const packageRoot = '**/api/v1/admin/billing/packages'
  const item = { packageId: 42, priceVersionId: id(31), displayName: 'Gói nghiên cứu giả lập', amountMinor: 25000,
    currency: 'VND', credits: 4, status: 'ACTIVE', version: 7 }
  await page.route(packageRoot + '?*', route => {
    expect(route.request().headers().authorization).toBe(`Bearer ${token}`)
    return route.fulfill({ json: { items: [item], nextCursor: null } })
  })
  await page.route(packageRoot + '/42/price-versions', route => {
    expect(route.request().headers()['idempotency-key']).toMatch(/^[0-9a-f-]{36}$/i)
    expect(route.request().postDataJSON()).toEqual({ expectedVersion: 7, displayName: item.displayName, amountMinor: 30000,
      currency: 'VND', credits: 4, reason: 'Giá kiểm thử' })
    return route.fulfill({ status: 409, json: { error: { code: 'ERR_CONCURRENT_UPDATE' } } })
  })
  await page.goto('/admin/catalog')
  await expect(page.getByRole('heading', { name: 'Quản trị hệ thống' })).toBeVisible()
  await page.getByText('Quản lý giá', { exact: true }).click()
  const editor = page.getByRole('group', { name: 'Tạo phiên bản giá' })
  await editor.getByLabel('Giá theo đơn vị nhỏ nhất').fill('30000')
  await editor.getByLabel('Lý do').fill('Giá kiểm thử')
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1)
  await editor.getByRole('button', { name: 'Lưu thay đổi' }).click()
  await expect(page.getByRole('alert')).toHaveText(/Phiên bản đã thay đổi/)
  await expect(page.getByText(item.displayName, { exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: 'Tải lại' }).click()
  await expect(page.getByText(item.displayName, { exact: true })).toBeVisible()
})

test('MOCK: small screens reflow and Admin tabs can be activated from the keyboard', async ({ page, context }) => {
  const viewports = [{ width: 390, height: 844 }, { width: 768, height: 1024 }]
  const assertReflow = (screen: Page) => expect.poll(() => screen.evaluate(() =>
    document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(1)

  for (const viewport of viewports) {
    await page.setViewportSize(viewport)
    await page.goto('/login')
    await expect(page.getByLabel('Email', { exact: true })).toBeVisible()
    await assertReflow(page)
  }
  await page.keyboard.press('Tab')
  const email = page.getByLabel('Email', { exact: true })
  await expect(email).toBeFocused()
  await expect(email).toHaveCSS('outline-style', 'solid')
  await fixture(page, ['PATIENT'])
  for (const [route, heading] of [
    ['/upload', 'AURA — Upload ảnh võng mạc'],
    ['/batches', 'Theo dõi batch ảnh võng mạc'],
  ]) {
    await page.goto(route)
    await expect(page.getByRole('heading', { name: heading })).toBeVisible()
    for (const viewport of viewports) {
      await page.setViewportSize(viewport)
      await assertReflow(page)
    }
  }
  const admin = await context.newPage()
  await fixture(admin, ['SYSTEM_ADMIN'])
  await admin.goto('/admin')
  await expect(admin.getByRole('heading', { name: 'Quản trị hệ thống (SuperAdmin)' })).toBeVisible()
  for (const viewport of viewports) {
    await admin.setViewportSize(viewport)
    await assertReflow(admin)
  }
  const tabs = admin.getByRole('tablist', { name: 'Danh mục quản trị' })
  const clinics = tabs.getByRole('tab', { name: 'Duyệt Cơ sở y tế' })
  const accounts = tabs.getByRole('tab', { name: 'Quản lý Tài khoản' })
  await clinics.press('ArrowRight')
  await expect(accounts).toBeFocused()
  await expect(clinics).toHaveAttribute('aria-selected', 'true')
  await admin.keyboard.press('Enter')
  await expect(accounts).toHaveAttribute('aria-selected', 'true')
  await expect(admin.getByRole('tabpanel', { name: 'Quản lý Tài khoản' })).toBeVisible()
  await admin.keyboard.press('Tab')
  await expect(admin.getByRole('searchbox', { name: 'Tìm tài khoản' })).toBeFocused()
})

test('MOCK: clinician opens the result in three clicks after selecting a clinic patient', async ({ page }) => {
  await fixture(page, ['DOCTOR'])
  const record = { patientId: patient, clinicId: clinic, fullName: 'Synthetic selected patient', status: 'ACTIVE' }
  const batchId = id(7), batchPath = '/api/v1/analysis-upload-batches'
  let item: { itemId: string; patientId: string; fileName: string; eyeSide: string; uploadStatus: string }
  let finalized = 0
  let resultReady = false
  let resultAllowed = true
  let resultReads = 0
  const inventory = () => ({
    batchId, statusUrl: `/api/v1/batches/${batchId}`, createdAt: '2026-11-01T09:00:00Z', status: 'UPLOADING', items: [item],
  })
  await page.route('**/api/v1/**', async route => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname
    expect(request.headers().authorization).toBe(`Bearer ${token}`)
    if (path === '/api/v1/clinics') {
      return route.fulfill({ json: { items: [{ clinicId: clinic, clinicName: 'Synthetic clinic', role: 'DOCTOR', status: 'ACTIVE' }] } })
    }
    if (path === `/api/v1/clinics/${clinic}/patients`) return route.fulfill({ json: { clinicId: clinic, items: [record] } })
    if (path === `/api/v1/clinics/${clinic}/patients/${patient}`) return route.fulfill({ json: record })
    if (path.startsWith(batchPath)) {
      expect(url.searchParams.get('clinicContextId')).toBe(clinic)
      if (path === batchPath && request.method() === 'POST') {
        const entries = request.postDataJSON().items
        expect(entries).toHaveLength(1)
        expect(entries[0]).toMatchObject({ patientId: patient, fileName: 'synthetic.png' })
        item = { ...entries[0], uploadStatus: 'DECLARED' }
        return route.fulfill({ status: 201, json: inventory() })
      }
      if (request.method() === 'PUT' && path === `${batchPath}/${batchId}/items/${item.itemId}`) {
        expect(request.postDataBuffer()?.includes(png)).toBe(true)
        item = { ...item, uploadStatus: 'STAGED' }
        return route.fulfill({ json: item })
      }
      if (request.method() === 'GET' && path === `${batchPath}/${batchId}`) {
        return route.fulfill({ json: inventory() })
      }
      if (path === `${batchPath}/${batchId}/finalize`) {
        expect(item.uploadStatus).toBe('STAGED')
        finalized++
        return route.fulfill({ status: 202, json: { ...inventory(),
          items: [{ analysisId: item.itemId, fileName: item.fileName, processingStatus: 'QUEUED' }] } })
      }
    }
    if (path === `/api/v1/analyses/${item?.itemId}`) {
      expect(url.searchParams.get('clinicContextId')).toBe(clinic)
      resultReads++
      if (!resultAllowed) return route.fulfill({ status: 403, json: { error: { code: 'ERR_FORBIDDEN' } } })
      return route.fulfill({ json: { analysisId: item.itemId, batchId, processingStatus: resultReady ? 'COMPLETED' : 'QUEUED',
        reviewStatus: 'PENDING', updatedAt: '2026-11-01T09:00:00Z',
        image: resultReady ? { fileId: file, kind: 'original' } : null,
        result: resultReady ? { version: 1, isMock: true, riskAvailable: false, riskLevel: null, riskScore: null,
          confidenceScore: null, modelVersion: 'synthetic', configVersion: 'fixture', thresholdVersion: 'fixture',
          disclaimer: 'Synthetic TEST', limitations: 'Software fixture', metrics: { avRatio: null },
          indicators: [], annotations: [], warnings: [{ code: 'AVR_UNAVAILABLE', message: 'Synthetic AVR unavailable reason' }] } : null,
      } })
    }
    if (path.startsWith(`/api/v1/analyses/${item?.itemId}/files/${file}/`)) {
      expect(url.searchParams.get('clinicContextId')).toBe(clinic)
      if (path.endsWith('/access')) return route.fulfill({ json: {
        url: `/api/v1/analyses/${item.itemId}/files/${file}/content?clinicContextId=${clinic}`,
        expiresAt: new Date(Date.now() + 60_000).toISOString(),
      } })
      if (path.endsWith('/content')) return route.fulfill({ contentType: 'image/png', body: png })
    }
    return route.fallback()
  })
  await page.goto('/clinics/patients')
  await page.getByRole('button', { name: 'Chi tiết', exact: true }).click()
  const selected = page.getByRole('region', { name: /Hồ Sơ Bệnh Án: Synthetic selected patient/ })
  await expect(selected).toBeVisible()
  const chooser = page.waitForEvent('filechooser')
  await selected.getByRole('button', { name: /Chụp \/ Gửi ảnh phân tích/ }).click()
  await (await chooser).setFiles({ name: 'synthetic.png', mimeType: 'image/png', buffer: png })
  await expect(page.getByRole('combobox', { name: 'Bệnh nhân ảnh1' })).toHaveValue(patient)
  await page.getByRole('button', { name: 'Gửi ảnh phòng khám' }).click()
  await page.getByRole('link', { name: 'Xem kết quả phân tích' }).click()
  await expect(page.getByRole('heading', { name: 'Kết quả phân tích võng mạc' })).toBeVisible()
  await expect(page.getByText(/Chưa có kết quả AI hoàn tất/)).toBeVisible()
  expect(finalized).toBe(1)

  resultReady = true
  await page.reload()
  const avr = page.getByRole('region', { name: 'Tỷ số động/tĩnh mạch (AVR)' })
  await expect(avr).toContainText('Synthetic AVR unavailable reason')
  await expect(avr).not.toContainText(/AVR:\s*0/)
  const image = page.getByRole('img', { name: 'Ảnh võng mạc gốc' })
  await expect(image).toHaveAttribute('src', /^blob:/)
  const blobUrl = (await image.getAttribute('src'))!
  await expect.poll(() => image.evaluate(img => (img as HTMLImageElement).naturalWidth)).toBeGreaterThan(0)

  await page.getByRole('link', { name: 'Trang chủ', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Trang chủ AURA' })).toBeVisible()
  await expect.poll(() => page.evaluate(url => fetch(url).then(() => true, () => false), blobUrl)).toBe(false)

  resultAllowed = false
  await page.goBack()
  await expect(page.getByRole('alert')).toContainText(/không được truy cập/i)
  await expect(image).toHaveCount(0)
  await expect(avr).toHaveCount(0)
  await page.getByRole('link', { name: 'Trang chủ', exact: true }).click()
  await page.getByRole('button', { name: 'Đăng xuất', exact: true }).click()
  await expect(page).toHaveURL(/\/login$/)
  const readsAfterLogout = resultReads
  await page.goBack()
  await expect(page).toHaveURL(/\/login$/)
  await expect(image).toHaveCount(0)
  expect(resultReads).toBe(readsAfterLogout)
})

for (const [width, height] of [[390, 844], [768, 1024], [1024, 900], [1366, 768], [1920, 1080]]) {
  test(`MOCK: Doctor approves through authenticated image bytes at ${width}px`, async ({ page }, info) => {
    const state = await fixture(page)
    await page.setViewportSize({ width, height })
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
  await page.getByRole('button', { name: 'Xác nhận và lưu đánh giá' }).press('Enter')
  const conflict = page.getByRole('alertdialog', { name: 'Xung đột phiên đánh giá' })
  await expect(conflict).toBeVisible()
  await expect(conflict).toHaveAccessibleDescription(/HTTP 409/)
  await expect(conflict.getByRole('button', { name: 'Đóng' })).toBeFocused()
  await page.keyboard.press('Tab')
  await expect(conflict.getByRole('button', { name: 'Tải phiên bản mới nhất' })).toBeFocused()
  await page.keyboard.press('Shift+Tab')
  await expect(conflict.getByRole('button', { name: 'Đóng' })).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(conflict).toHaveCount(0)
  const refresh = page.getByRole('button', { name: 'Làm mới chi tiết' })
  await expect(refresh).toBeFocused()
  expect(state.posts).toBe(2)
  await refresh.press('Enter')
  await expect(page.getByRole('heading', { name: 'Chi tiết ca chụp' })).toBeFocused()
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
          expect(request.arguments).toEqual([conversationId])
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
