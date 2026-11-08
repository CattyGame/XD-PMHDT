import { expect, test, type Page } from '@playwright/test'

// Các luồng có xác thực chỉ chạy khi được cung cấp tài khoản TEST đúng quyền.
// Không dùng tài khoản sản xuất để tạo/sửa review hoặc phát sinh thanh toán.
const email = process.env.SCRUM357_TEST_EMAIL
const password = process.env.SCRUM357_TEST_PASSWORD
const doctorClinicId = process.env.SCRUM357_DOCTOR_CLINIC_ID
const batchId = process.env.SCRUM357_BATCH_ID
const guid = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i

async function login(page: Page) {
  if (!email || !password) throw new Error('Thiếu SCRUM357_TEST_EMAIL / SCRUM357_TEST_PASSWORD')
  await page.goto('/login')
  await page.getByLabel('Email', { exact: true }).fill(email)
  await page.getByLabel('Mật khẩu', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Đăng nhập bằng Email' }).click()
  await expect(page).toHaveURL(/\/$/, { timeout: 25_000 })
}

// AC1: kiểm tra trực tiếp tại 1024/1366/1920 px, tạo ảnh làm bằng chứng.
// Các trang được bảo vệ cần tài khoản phù hợp, không giả mạo quyền.
for (const width of [1024, 1366, 1920]) {
  test(`AC1: trang đăng nhập không tràn ngang ở ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 })
    await page.goto('/login')
    await expect(page.getByRole('heading', { name: /AURA — Đăng nhập/ })).toBeVisible()
    await page.screenshot({ path: testInfo.outputPath(`login-${width}.png`), fullPage: true })
    const dimensions = await page.evaluate(() => ({
      viewport: document.documentElement.clientWidth,
      document: document.documentElement.scrollWidth,
    }))
    expect(dimensions.document, `Tràn ngang ${width}px`).toBeLessThanOrEqual(dimensions.viewport + 1)
  })
}

for (const [name, route] of [
  ['Trang lỗi phân quyền', '/403'],
  ['Trang lỗi máy chủ', '/500'],
] as const) {
  test(`AC1: ${name} không tràn ngang ở 1024px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1024, height: 768 })
    await page.goto(route)
    await page.screenshot({ path: testInfo.outputPath(`${route.slice(1)}-1024.png`), fullPage: true })
    const overflow = await page.evaluate(() =>
      document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow).toBeLessThanOrEqual(1)
  })
}


// Chạy sau khi 3 PR phụ thuộc đã được tích hợp vào một môi trường test.
// Thiếu route phải FAIL thay vì chụp một trang 404 rồi tự nhận PASS.
for (const { label, route, heading } of [
  { label: 'Bulk Upload', route: '/clinic-upload', heading: 'Tải ảnh phòng khám' },
  { label: 'Batch Progress', route: '/batches', heading: 'Theo dõi batch ảnh võng mạc' },
  { label: 'Billing Packages', route: '/billing/packages', heading: 'Mua gói khám' },
] as const) {
  test(`AC1: ${label} responsive ở 1024/1366/1920`, async ({ page }, testInfo) => {
    test.skip(!email || !password, 'Thiếu tài khoản TEST để vào màn hình được bảo vệ')
    await login(page)
    for (const width of [1024, 1366, 1920]) {
      await page.setViewportSize({ width, height: 900 })
      await page.goto(route)
      await expect(page.getByRole('heading', { name: heading })).toBeVisible()
      const overflow = await page.evaluate(() =>
        document.documentElement.scrollWidth - document.documentElement.clientWidth)
      expect(overflow, `${label} tràn ngang ở ${width}px`).toBeLessThanOrEqual(1)
      await page.screenshot({ path: testInfo.outputPath(`${label.toLowerCase().replace(/ /g, '-')}-${width}.png`), fullPage: true })
    }
  })
}

test('AC1: Doctor Workspace responsive 1024px (tài khoản DOCTOR)', async ({ page }, testInfo) => {
  test.skip(!email || !password || !guid.test(doctorClinicId || ''),
    'Chưa có tài khoản bác sĩ TEST và SCRUM357_DOCTOR_CLINIC_ID')
  await login(page)
  await page.setViewportSize({ width: 1024, height: 900 })
  await page.goto('/doctor/workspace')
  await expect(page.getByRole('heading', { name: 'Không gian làm việc Bác sĩ' })).toBeVisible()
  await page.getByRole('textbox', { name: 'Clinic Context ID' }).fill(doctorClinicId!)
  await page.getByRole('button', { name: 'Tải worklist' }).click()
  await expect(page.getByRole('region', { name: 'Danh sách ca chụp' })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('doctor-workspace-1024.png'), fullPage: true })
  const overflow = await page.evaluate(() =>
    document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(1)
})

test('AC2: Batch tiếp tục polling sau khi offline/online, không F5', async ({ page }, testInfo) => {
  test.skip(!email || !password || !guid.test(batchId || ''),
    'Chưa có tài khoản TEST và SCRUM357_BATCH_ID thuộc quyền truy cập')
  await login(page)
  await page.goto(`/batches/${batchId}`)
  await expect(page.getByRole('heading', { name: 'Theo dõi batch ảnh võng mạc' })).toBeVisible()
  // Test kết nối thực: chỉ GET và khôi phục polling, không upload/tạo order.
  await page.context().setOffline(true)
  // Kích hoạt một GET theo nút thủ công; KHÔNG reload trang trong lúc offline.
  await page.getByRole('button', { name: /Tải lại trạng thái/ }).click()
  await expect(page.getByText(/Mất kết nối|Đang thử lại|Không thể/i).first()).toBeVisible({ timeout: 20_000 })
  await page.context().setOffline(false)
  await page.evaluate(() => window.dispatchEvent(new Event('online')))
  await expect(page.getByText(/Đang xử lý|Batch đã kết thúc|trạng thái sẽ tự cập nhật|Backend đã tiếp nhận/i).first())
    .toBeVisible({ timeout: 30_000 })
  await page.screenshot({ path: testInfo.outputPath('batch-reconnected.png'), fullPage: true })
})

test('AC3: Doctor review, mô phỏng 409 và không ghi đè (chỉ tài khoản TEST)', async ({ page }, testInfo) => {
  test.skip(!email || !password || !guid.test(doctorClinicId || ''),
    'Thiếu tài khoản DOCTOR TEST và clinic ID')
  await login(page)
  await page.goto('/doctor/workspace')
  await page.getByRole('textbox', { name: 'Clinic Context ID' }).fill(doctorClinicId!)
  await page.getByRole('button', { name: 'Tải worklist' }).click()
  await expect(page.getByRole('button', { name: 'Mở chi tiết' }).first()).toBeVisible()
  await page.getByRole('button', { name: 'Mở chi tiết' }).first().click()
  await expect(page.getByRole('heading', { name: 'Chi tiết ca chụp' })).toBeVisible()
  const submit = page.getByRole('button', { name: 'Xác nhận và lưu đánh giá' })
  test.skip(!(await submit.isVisible()) || !(await submit.isEnabled()),
    'Chưa có ca COMPLETED chưa review được phân quyền trong môi trường TEST')
  let intercepted = 0
  await page.route(/\/api\/v1\/doctor\/analyses\/[^/]+\/reviews\?clinicContextId=/, async route => {
    intercepted++
    await route.fulfill({ status: 409, contentType: 'application/json',
      body: JSON.stringify({ error: { code: 'ERR_CONFLICT', message: 'Conflict' } }) })
  })
  await submit.click()
  await expect(page.getByRole('alertdialog', { name: 'Xung đột phiên đánh giá' })).toBeVisible()
  await expect(page.getByText(/Dữ liệu cũ đã bị khóa/)).toBeVisible()
  expect(intercepted).toBe(1)
  await page.screenshot({ path: testInfo.outputPath('doctor-conflict-409.png'), fullPage: true })
  // 409 này được mock trong trình duyệt; chưa phải E2E backend đa phiên thật.
})
