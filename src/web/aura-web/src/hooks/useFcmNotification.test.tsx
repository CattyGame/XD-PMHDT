import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BackendContext } from '../api/backendContext'
import { BackendSessionContext } from '../auth/backendSession'
import { useFcmNotification } from './useFcmNotification'

const mocks = vi.hoisted(() => ({
  listen: vi.fn(), refresh: vi.fn(), request: vi.fn(), permission: vi.fn(),
}))
vi.mock('../api/client', () => ({ isMockEnabled: false }))
vi.mock('../firebase/messaging', () => ({ listenForegroundMessages: mocks.listen }))
vi.mock('../firebase/tokenLifecycle', () => ({ refreshFcmToken: mocks.refresh }))
vi.mock('../firebase/notificationPermission', () => ({
  getNotificationPermission: mocks.permission, requestNotificationPermission: mocks.request,
}))
const context: BackendContext = {
  firebaseUid: 'uid-a', accountId: '11111111-1111-4111-8111-111111111111',
  patientId: '22222222-2222-4222-8222-222222222222',
  personalTenantId: '33333333-3333-4333-8333-333333333333',
  roles: ['PATIENT'], displayName: 'Người dùng', profileVersion: 1,
}
function Probe() {
  const { enable, permission, message, toast, closeToast } = useFcmNotification()
  return <>
    <button onClick={() => void enable()}>Bật</button>
    <button onClick={closeToast}>Đóng</button>
    <p>{permission}</p><p role="status">{message}</p>
    {toast && <div data-testid="toast">{toast.title}</div>}
  </>
}
function show() {
  return render(<BackendSessionContext.Provider value={{ state: { status: 'ready', context },
    logout: vi.fn(async () => {}), retry: vi.fn() }}><Probe /></BackendSessionContext.Provider>)
}
beforeEach(() => {
  vi.resetAllMocks()
  Object.defineProperty(window, 'isSecureContext', { configurable: true, value: true })
  Object.defineProperty(navigator, 'serviceWorker', { configurable: true, value: {} })
  mocks.permission.mockReturnValue('granted')
  mocks.request.mockResolvedValue('granted')
  mocks.refresh.mockResolvedValue({ status: 'ready' })
  mocks.listen.mockResolvedValue(vi.fn())
})
afterEach(() => cleanup())
describe('SCRUM-355 - useFcmNotification', () => {
  it('AC2: chỉ yêu cầu quyền và đồng bộ token sau click', async () => {
    show()
    expect(mocks.request).not.toHaveBeenCalled()
    expect(mocks.refresh).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Bật' }))
    await waitFor(() => expect(mocks.refresh).toHaveBeenCalledTimes(1))
    expect(mocks.request).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('status').textContent).toContain('Đã đăng ký FCM Token')
  })
  it('từ chối quyền thông báo và không gửi token về backend', async () => {
    mocks.permission.mockReturnValue('denied')
    mocks.request.mockResolvedValue('denied')
    show()
    fireEvent.click(screen.getByRole('button', { name: 'Bật' }))
    await waitFor(() => expect(screen.getByRole('status').textContent).toContain('bị chặn'))
    expect(mocks.refresh).not.toHaveBeenCalled()
  })
  it('nhận push foreground và lọc nội dung nhạy cảm', async () => {
    let callback: ((payload: import('firebase/messaging').MessagePayload) => void) | undefined
    mocks.listen.mockImplementation(async cb => { callback = cb; return vi.fn() })
    show()
    await waitFor(() => expect(callback).toBeDefined())
    callback?.({ from: 'test-sender', collapseKey: 'test', messageId: 'unique-1', notification: { title: 'Tên bệnh nhân', body: 'Bí mật' },
      data: { targetPath: '/analyses/11111111-1111-4111-8111-111111111111',
        analysisId: '11111111-1111-4111-8111-111111111111' } })
    expect((await screen.findByTestId('toast')).textContent).toContain('AURA - Có thông báo mới')
  })
  it('báo lỗi backend khi đăng ký token không thành công', async () => {
    mocks.refresh.mockRejectedValueOnce(new Error('service unavailable'))
    show(); fireEvent.click(screen.getByRole('button', { name: 'Bật' }))
    await waitFor(() => expect(screen.getByRole('status').textContent).toContain('Không đăng ký được'))
  })
})
