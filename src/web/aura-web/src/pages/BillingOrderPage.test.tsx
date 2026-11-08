import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import type { BackendContext } from '../api/backendContext'
import { BackendSessionContext } from '../auth/backendSession'
const mocks = vi.hoisted(() => ({ auth: vi.fn() }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
import BillingOrderPage from './BillingOrderPage'
const id = '40000000-0000-4000-8000-000000000004'
const context: BackendContext = { firebaseUid: 'user-a', accountId: id, patientId: id,
  personalTenantId: id, roles: ['PATIENT'], displayName: 'A', profileVersion: 1 }
const user = { uid: 'user-a', getIdToken: vi.fn(async () => 'verified-token') }
const fetchMock = vi.fn<typeof fetch>()
function view(uid = 'user-a') {
  return <BackendSessionContext.Provider value={{ state: { status: 'ready', context: { ...context, firebaseUid: uid } }, retry: vi.fn(), logout: vi.fn() }}>
    <MemoryRouter initialEntries={[`/billing/orders/${id}`]}><Routes>
      <Route path="/billing/orders/:orderId" element={<BillingOrderPage />} />
    </Routes></MemoryRouter>
  </BackendSessionContext.Provider>
}
beforeEach(() => {
  vi.resetAllMocks(); user.getIdToken.mockResolvedValue('verified-token')
  mocks.auth.mockReturnValue({ currentUser: user }); vi.stubGlobal('fetch', fetchMock)
})
afterEach(() => { cleanup(); vi.unstubAllGlobals() })
it('loads the authorized order with bearer and clears it when the session changes', async () => {
  fetchMock.mockResolvedValue(new Response(JSON.stringify({ id, status: 'COMPLETED', packageNameSnapshot: 'Purchased package',
    priceAmountSnapshot: 100, currencyCodeSnapshot: 'VND', creditsSnapshot: 10 })))
  const page = render(view())
  expect(await screen.findByText('Purchased package')).toBeTruthy()
  expect(fetchMock).toHaveBeenCalledWith(`/api/v1/billing/orders/${id}`, expect.objectContaining({
    headers: { Authorization: 'Bearer verified-token' }, redirect: 'error', credentials: 'omit', cache: 'no-store',
  }))
  page.rerender(view('user-b'))
  expect(screen.queryByText('Purchased package')).toBeNull()
  expect(await screen.findByRole('alert')).toBeTruthy()
  expect(fetchMock).toHaveBeenCalledTimes(1)
})
it('shows access denial without exposing an order on HTTP 404', async () => {
  fetchMock.mockResolvedValue(new Response('{}', { status: 404 }))
  render(view())
  expect((await screen.findByRole('alert')).textContent).toContain('không có quyền')
  expect(screen.queryByText('Purchased package')).toBeNull()
})
