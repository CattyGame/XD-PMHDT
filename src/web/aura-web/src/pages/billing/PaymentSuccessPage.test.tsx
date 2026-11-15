import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { BackendContext } from '../../api/backendContext'
import { BackendSessionContext } from '../../auth/backendSession'
import PaymentSuccessPage from './PaymentSuccessPage'

const mocks = vi.hoisted(() => ({ order: vi.fn(), wallet: vi.fn() }))
vi.mock('../../api/billing', async importOriginal => ({
  ...await importOriginal<typeof import('../../api/billing')>(), getBillingOrder: mocks.order, getBillingWallet: mocks.wallet,
}))
const id = '22222222-2222-4222-8222-222222222222'
const context: BackendContext = {
  accountId: '11111111-1111-4111-8111-111111111111', firebaseUid: 'uid',
  patientId: '33333333-3333-4333-8333-333333333333',
  personalTenantId: '44444444-4444-4444-8444-444444444444',
  roles: ['PATIENT'], displayName: 'A', profileVersion: 1,
}
function show(withEvidence: boolean) {
  return render(<BackendSessionContext.Provider value={{ state: { status: 'ready', context },
    logout: vi.fn(async () => {}), retry: vi.fn() }}>
    <MemoryRouter initialEntries={[{ pathname: `/billing/success/${id}`, state: withEvidence
      ? { payment: { orderId: id, transactionId: 'tx', status: 'COMPLETED',
          message: 'OK', creditsAdded: 5, balanceAfter: 9, replayed: false } } : null }]}>
      <Routes><Route path="/billing/success/:orderId" element={<PaymentSuccessPage />} /></Routes>
    </MemoryRouter>
  </BackendSessionContext.Provider>)
}
beforeEach(() => { vi.clearAllMocks(); mocks.order.mockResolvedValue({ id, status: 'COMPLETED',
  billingOwnerType: 'ACCOUNT', billingOwnerId: context.accountId, packageNameSnapshot: 'Gói thử', creditsSnapshot: 5 }); mocks.wallet.mockResolvedValue({ balance: 9, environment: 'TEST' }) })
afterEach(() => cleanup())
describe('SCRUM-355 - Xác nhận thanh toán', () => {
  it('AC1: reads the current wallet after confirmed payment', async () => {
    show(true)
    await screen.findByText('Thanh toán đã được backend xác nhận')
    expect(await screen.findByText('9')).toBeTruthy()
    expect(await screen.findByText(/Số dư ví hiện tại/)).toBeTruthy()
  })
  it('sau refresh không tự tính số dư từ order', async () => {
    show(false)
    await screen.findByText('Thanh toán đã được backend xác nhận')
    expect(await screen.findByText('9')).toBeTruthy()
    expect(mocks.wallet).toHaveBeenCalledWith(context, { type: 'ACCOUNT', id: context.accountId }, expect.any(AbortSignal))
  })
  it('không hiển thị cộng tiền khi backend chỉ trả PENDING', async () => {
    mocks.order.mockResolvedValueOnce({ id, status: 'PENDING' })
    show(true)
    await screen.findByText('Chưa ghi nhận thanh toán thành công')
    expect(mocks.wallet).not.toHaveBeenCalled()
  })
})
