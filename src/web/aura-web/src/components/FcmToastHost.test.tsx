import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import type { BackendContext } from '../api/backendContext'
import { BackendSessionContext } from '../auth/backendSession'
import FcmToastHost from './FcmToastHost'

const mocks = vi.hoisted(() => ({ hook: vi.fn() }))
vi.mock('../hooks/useFcmNotification', () => ({ useFcmNotification: mocks.hook }))
const id = '11111111-1111-4111-8111-111111111111'
const context: BackendContext = {
  accountId: '22222222-2222-4222-8222-222222222222', firebaseUid: 'uid',
  patientId: '33333333-3333-4333-8333-333333333333',
  personalTenantId: '44444444-4444-4444-8444-444444444444',
  roles: ['PATIENT'], displayName: 'A', profileVersion: 1,
}
function LocationProbe() { const location = useLocation(); return <p data-testid="path">{location.pathname}</p> }
function show() {
  return render(<BackendSessionContext.Provider value={{ state: { status: 'ready', context },
    retry: vi.fn(), logout: vi.fn(async () => {}) }}>
    <MemoryRouter initialEntries={['/notifications']}><FcmToastHost /><LocationProbe />
      <Routes><Route path="/analyses/:analysisId" element={<p>Trang kết quả thật</p>} /></Routes>
    </MemoryRouter>
  </BackendSessionContext.Provider>)
}
beforeEach(() => { vi.clearAllMocks(); mocks.hook.mockReturnValue({
  permission: 'granted', enabled: true, busy: false, message: '', guidance: 'Đã cấp quyền',
  enable: vi.fn(), closeToast: vi.fn(),
  toast: { title: 'Phân tích ảnh đáy mắt hoàn tất', body: 'Mở AURA để xem.',
    targetPath: `/analyses/${id}`, id: 'm1' },
}) })
afterEach(() => cleanup())
describe('SCRUM-355 FCM foreground toast và click AC3', () => {
  it('hiển thị toast và điều hướng đúng ca phân tích', () => {
    show()
    expect(screen.getByText('Phân tích ảnh đáy mắt hoàn tất')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Xem kết quả' }))
    expect(screen.getByTestId('path').textContent).toBe(`/analyses/${id}`)
    expect(screen.getByText('Trang kết quả thật')).toBeTruthy()
  })
  it('cho đóng toast nhưng không điều hướng', () => {
    show(); fireEvent.click(screen.getByRole('button', { name: 'Đóng thông báo' }))
    expect(mocks.hook().closeToast).toHaveBeenCalledTimes(1)
    expect(screen.getByTestId('path').textContent).toBe('/notifications')
  })
})
