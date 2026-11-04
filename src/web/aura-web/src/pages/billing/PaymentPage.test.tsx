import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { BackendContext } from '../../api/backendContext'
import { BackendSessionContext } from '../../auth/backendSession'
import PaymentPage from './PaymentPage'

const mocks = vi.hoisted(() => ({ order: vi.fn(), pay: vi.fn() }))
vi.mock('../../api/billing', async importOriginal => ({
  ...await importOriginal<typeof import('../../api/billing')>(),
  getBillingOrder: mocks.order, paySandbox: mocks.pay,
}))
const orderId = '22222222-2222-4222-8222-222222222222'
const context: BackendContext = { accountId: '11111111-1111-4111-8111-111111111111',
  patientId: '33333333-3333-4333-8333-333333333333', personalTenantId: '44444444-4444-4444-8444-444444444444',
  firebaseUid: 'uid', roles: ['PATIENT'], displayName: 'A', profileVersion: 1 }
function show() {
  return render(<BackendSessionContext.Provider value={{ state: { status: 'ready', context },
    logout: vi.fn(async () => {}), retry: vi.fn() }}>
    <MemoryRouter initialEntries={[`/billing/pay/${orderId}`]}><Routes>
      <Route path="/billing/pay/:orderId" element={<PaymentPage />} />
      <Route path="/billing/success/:orderId" element={<p>Đã chuyển đến trang thành công</p>} />
    </Routes></MemoryRouter>
  </BackendSessionContext.Provider>)
}
beforeEach(() => {
  vi.resetAllMocks()
  mocks.order.mockResolvedValue({ id: orderId, status: 'PENDING', environment: 'TEST',
    packageNameSnapshot: 'Gói thử', priceAmountSnapshot: 50000, currencyCodeSnapshot: 'VND', creditsSnapshot: 2 })
  mocks.pay.mockResolvedValue({ orderId, status: 'COMPLETED', transactionId: 'sandbox',
    creditsAdded: 2, balanceAfter: 5, replayed: false })
})
afterEach(() => cleanup())

describe('SCRUM-355 - Payment Sandbox UI', () => {
  it('không tự POST thanh toán khi load/refresh; chỉ đọc GET', async () => {
    show(); await screen.findByText('Gói thử')
    expect(mocks.pay).not.toHaveBeenCalled()
  })
  it('chuyển thành công sau khi backend trả COMPLETED', async () => {
    show()
    fireEvent.click(await screen.findByRole('button', { name: 'Thanh toán thành công (Sandbox)' }))
    await screen.findByText('Đã chuyển đến trang thành công')
    expect(mocks.pay).toHaveBeenCalledTimes(1)
    expect(mocks.pay.mock.calls[0][1]).toBe(orderId)
  })
  it('không ghi nhận thành công khi backend trả CANCELLED', async () => {
    mocks.pay.mockResolvedValue({ orderId, status: 'CANCELLED', creditsAdded: 0, balanceAfter: 0 })
    mocks.order.mockResolvedValueOnce({ id: orderId, status: 'PENDING', environment: 'TEST', packageNameSnapshot: 'Gói thử',
      priceAmountSnapshot: 50000, currencyCodeSnapshot: 'VND', creditsSnapshot: 2 }).mockResolvedValueOnce({
      id: orderId, status: 'CANCELLED', environment: 'TEST', packageNameSnapshot: 'Gói thử',
      priceAmountSnapshot: 50000, currencyCodeSnapshot: 'VND', creditsSnapshot: 2,
    })
    show()
    fireEvent.click(await screen.findByRole('button', { name: 'Mô phỏng thất bại' }))
    await waitFor(() => expect(screen.queryByText('Đã chuyển đến trang thành công')).toBeNull())
    expect(mocks.pay).toHaveBeenCalledTimes(1)
  })
  it('khóa POST sau lỗi mạng không rõ kết quả để ngăn thanh toán trùng', async () => {
    mocks.pay.mockRejectedValueOnce(new Error('timeout'))
    show()
    fireEvent.click(await screen.findByRole('button', { name: 'Thanh toán thành công (Sandbox)' }))
    await screen.findByText(/Phản hồi thanh toán không rõ ràng/)
    const button = screen.getByRole('button', { name: 'Thanh toán thành công (Sandbox)' }) as HTMLButtonElement
    expect(button.disabled).toBe(true)
    expect(mocks.pay).toHaveBeenCalledTimes(1)
  })
})
