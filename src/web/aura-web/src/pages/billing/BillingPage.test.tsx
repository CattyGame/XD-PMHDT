import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { BackendContext } from '../../api/backendContext'
import { BillingError } from '../../api/billing'
import { BackendSessionContext } from '../../auth/backendSession'
import BillingPage from './BillingPage'

const mocks = vi.hoisted(() => ({ wallet: vi.fn(), packages: vi.fn(), clinics: vi.fn(), create: vi.fn() }))
vi.mock('../../api/billing', async importOriginal => ({
  ...await importOriginal<typeof import('../../api/billing')>(),
  getBillingPackages: mocks.packages, getBillingWallet: mocks.wallet, createBillingOrder: mocks.create,
}))
vi.mock('../../api/clinicUpload', () => ({ getUploadClinics: mocks.clinics }))
const id = '11111111-1111-4111-8111-111111111111'
const orderId = '22222222-2222-4222-8222-222222222222'
const priceVersionId = '77777777-7777-4777-8777-777777777777'
const context: BackendContext = {
  firebaseUid: 'uid', accountId: id, patientId: '33333333-3333-4333-8333-333333333333',
  personalTenantId: '44444444-4444-4444-8444-444444444444', roles: ['PATIENT'],
  displayName: 'Người dùng', profileVersion: 1,
}
function show() {
  return render(<BackendSessionContext.Provider value={{ state: { status: 'ready', context },
    logout: vi.fn(async () => {}), retry: vi.fn() }}>
    <MemoryRouter initialEntries={['/billing/packages']}><Routes>
      <Route path="/billing/packages" element={<BillingPage />} />
      <Route path="/billing/pay/:orderId" element={<p>Đã mở đơn thanh toán</p>} />
    </Routes></MemoryRouter>
  </BackendSessionContext.Provider>)
}
beforeEach(() => {
  vi.clearAllMocks()
  mocks.packages.mockResolvedValue({ data: [{ id: 7, name: 'Gói thử nghiệm', price: 90000, credits: 3, priceVersionId, currency: 'VND', version: 1 }],
    source: 'postgres', cacheHit: false, ttlSeconds: 0, databaseReadCount: 1 })
  mocks.clinics.mockResolvedValue([])
  mocks.wallet.mockRejectedValue(new Error('wallet unavailable'))
  mocks.create.mockResolvedValue({ orderId })
})
afterEach(() => cleanup())
describe('SCRUM-355 - BillingPage', () => {
  it('hiển thị giá theo phiên bản và không bịa số dư ví', async () => {
    show()
    await screen.findByText('Gói thử nghiệm')
    expect(screen.getByText(/đúng phiên bản giá/)).toBeTruthy()
    expect(screen.getByText('Chưa có dữ liệu')).toBeTruthy()
    expect(mocks.create).not.toHaveBeenCalled()
  })
  it('chỉ tạo đơn sau khi bấm Mua gói, điều hướng khi backend xác nhận', async () => {
    show()
    fireEvent.click(await screen.findByRole('button', { name: 'Mua gói' }))
    expect(await screen.findByText('Đã mở đơn thanh toán')).toBeTruthy()
    expect(mocks.create).toHaveBeenCalledTimes(1)
    expect(mocks.create.mock.calls[0][2]).toBe(7)
    expect(mocks.create.mock.calls[0][3]).toMatch(/^[0-9a-f-]{36}$/i)
    expect(mocks.create.mock.calls[0][4]).toBe(priceVersionId)
  })
  it('dùng lại cùng Idempotency-Key khi request lỗi và người dùng thử lại', async () => {
    mocks.create.mockRejectedValueOnce(new Error('network')).mockRejectedValueOnce(new Error('network'))
    show()
    const button = await screen.findByRole('button', { name: 'Mua gói' })
    fireEvent.click(button)
    await screen.findByRole('alert')
    fireEvent.click(button)
    await waitFor(() => expect(mocks.create).toHaveBeenCalledTimes(2))
    expect(mocks.create.mock.calls[0][3]).toBe(mocks.create.mock.calls[1][3])
  })
  it('tải lại giá sau xung đột và chờ người dùng bấm mua lần nữa', async () => {
    mocks.create.mockRejectedValueOnce(new BillingError(409, 'ERR_PRICE_VERSION_CHANGED'))
    show()
    fireEvent.click(await screen.findByRole('button', { name: 'Mua gói' }))
    expect(await screen.findByText(/Giá gói đã thay đổi/)).toBeTruthy()
    await waitFor(() => expect(mocks.packages).toHaveBeenCalledTimes(2))
    expect(mocks.create).toHaveBeenCalledTimes(1)
  })
})


it('audit: an ACTIVE ClinicAdmin with global PATIENT must be offered the clinic wallet', async () => {
  mocks.clinics.mockResolvedValue([{ clinicId: id, clinicName: 'Clinic fixture', role: 'CLINIC_ADMIN', status: 'ACTIVE' }])
  show(); await screen.findByText('Gói thử nghiệm')
  await waitFor(() => expect(mocks.clinics).toHaveBeenCalledOnce())
  expect(screen.getByRole('radio', { name: 'Ví tổ chức' })).toBeTruthy()
})
