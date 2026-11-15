import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Link, MemoryRouter, useLocation, useNavigate } from 'react-router-dom'
import type { User } from 'firebase/auth'
import { BackendSessionContext } from '../auth/backendSession'
import BackendSessionProvider from '../auth/BackendSessionProvider'
import type { BackendSessionState } from '../auth/backendSessionStore'
import { BackendContextError, type BackendContext } from '../api/backendContext'
import ApplicationRoutes from './ApplicationRoutes'

const mocks = vi.hoisted(() => ({ auth: vi.fn(), observe: vi.fn(), signOut: vi.fn(), load: vi.fn(), interceptRoot: false, rootRender: vi.fn() }))
vi.mock('react-dom/client', async (importOriginal) => {
  const original = await importOriginal<typeof import('react-dom/client')>()
  return { ...original, createRoot: (...args: Parameters<typeof original.createRoot>) =>
    mocks.interceptRoot ? { render: mocks.rootRender, unmount: vi.fn() } : original.createRoot(...args) }
})
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('firebase/auth', () => ({
  onAuthStateChanged: mocks.observe, signOut: mocks.signOut,
  GoogleAuthProvider: vi.fn(), signInWithEmailAndPassword: vi.fn(), signInWithPopup: vi.fn(),
  browserLocalPersistence: {}, setPersistence: vi.fn(),
}))
vi.mock('../api/backendContext', async (importOriginal) => ({
  ...await importOriginal<typeof import('../api/backendContext')>(), loadBackendContext: mocks.load,
}))
vi.mock('../App', () => ({ default: () => <p>Dashboard minh họa cũ</p> }))
vi.mock('./AppRouter', () => ({ default: () => <p>Router minh họa cũ</p> }))
vi.mock('../pages/UploadPage', () => ({ default: () => <p>Form upload cá nhân</p> }))

const context: BackendContext = {
  accountId: '11111111-1111-4111-8111-111111111111',
  patientId: '22222222-2222-4222-8222-222222222222',
  personalTenantId: '33333333-3333-4333-8333-333333333333',
  firebaseUid: 'synthetic-session', roles: ['PATIENT'], displayName: 'Hồ sơ phiên hiện tại', profileVersion: 1,
}

function HistoryProbe() {
  const location = useLocation(); const navigate = useNavigate()
  return <div>
    <span data-testid="current-route">{location.pathname}</span>
    <Link to="/">Về nhà kiểm thử</Link>
    <button onClick={() => void navigate(-1)}>Lùi lịch sử kiểm thử</button>
  </div>
}

function show(state: BackendSessionState, path = '/') {
  return render(
    <BackendSessionContext.Provider value={{ state, retry: vi.fn(), logout: vi.fn(async () => {}) }}>
      <MemoryRouter initialEntries={[path]}><ApplicationRoutes /><HistoryProbe /></MemoryRouter>
    </BackendSessionContext.Provider>,
  )
}

beforeEach(() => { vi.clearAllMocks(); mocks.auth.mockReturnValue(null) })
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('Điều hướng tài khoản thật và khu vực minh họa', () => {
  it('accepts an invitation explicitly after login then refreshes clinic discovery', async () => {
    const membershipId = '11111111-1111-4111-8111-700000000001'
    mocks.auth.mockReturnValue({ currentUser: { uid: context.firebaseUid, getIdToken: async () => 'fresh-user-token' } })
    const fetchMock = vi.fn<typeof fetch>().mockImplementation(async input => new Response(JSON.stringify(
      String(input).endsWith('/accept') ? { message: 'Accepted' } : { items: [{ clinicId: membershipId, clinicName: 'Accepted clinic', role: 'STAFF', status: 'ACTIVE' }] }),
      { status: 200, headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)
    show({ status: 'ready', context }, `/clinics/invitations/${membershipId}`)
    fireEvent.change(screen.getByLabelText('Token lời mời'), { target: { value: 'one-use-token' } })
    fireEvent.click(screen.getByRole('button', { name: 'Chấp nhận lời mời' }))
    await screen.findByText('Accepted clinic')
    expect(fetchMock).toHaveBeenCalledWith(`/api/v1/clinics/invitations/${membershipId}/accept`, expect.objectContaining({
      method: 'POST', body: JSON.stringify({ token: 'one-use-token' }), headers: expect.objectContaining({ Authorization: 'Bearer fresh-user-token' }),
    }))
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/clinics', expect.anything())
    expect(context.roles).not.toContain('DOCTOR')
  })
  it.each(['/', '/upload', '/demo'])('chặn route %s khi anonymous', (path) => {
    show({ status: 'anonymous' }, path)
    expect(screen.getByRole('heading', { name: 'AURA — Đăng nhập' })).toBeTruthy()
    expect(screen.getByTestId('current-route').textContent).toBe('/login')
    expect(screen.queryByText('Dashboard minh họa cũ')).toBeNull()
  })

  it('giữ loading trên route hiện tại, không hiển thị demo hoặc danh tính', () => {
    show({ status: 'loading', firebaseUid: null })
    expect(screen.getByRole('status').textContent).toContain('Đang xác nhận')
    expect(screen.getByTestId('current-route').textContent).toBe('/')
    expect(screen.queryByText('Dashboard minh họa cũ')).toBeNull()
    expect(screen.queryByRole('heading', { name: 'Trang chủ AURA' })).toBeNull()
  })

  it('hiển thị error chuẩn trước khi cho vào route được bảo vệ', () => {
    show({ status: 'error', firebaseUid: context.firebaseUid,
      error: new BackendContextError(403, 'ERR_ACCOUNT_DISABLED') })
    expect(screen.getByRole('alert').textContent).toContain('đã bị khóa')
    expect(screen.queryByText(context.displayName)).toBeNull()
    expect(screen.queryByText('Dashboard minh họa cũ')).toBeNull()
  })

  it('vào trang chủ thật tại / và không render Dashboard demo', () => {
    show({ status: 'ready', context })
    expect(screen.getByRole('heading', { name: 'Trang chủ AURA' })).toBeTruthy()
    expect(screen.getByText('Bệnh nhân')).toBeTruthy()
    expect(screen.queryByText('Dashboard minh họa cũ')).toBeNull()
    expect(screen.queryByRole('note')).toBeNull()
  })

  it('login chỉ điều hướng về trang chủ thật khi backend-ready', () => {
    show({ status: 'ready', context }, '/login')
    expect(screen.getByTestId('current-route').textContent).toBe('/')
    expect(screen.getByRole('heading', { name: 'Trang chủ AURA' })).toBeTruthy()
  })

  it('mở đúng /upload từ trang chủ thật', () => {
    show({ status: 'ready', context })
    fireEvent.click(screen.getByRole('link', { name: 'Mở trang tải ảnh' }))
    expect(screen.getByTestId('current-route').textContent).toBe('/upload')
    expect(screen.getByText('Form upload cá nhân')).toBeTruthy()
    expect(screen.queryByText('Dashboard minh họa cũ')).toBeNull()
  })

  it('Dashboard cũ chỉ xuất hiện trong /demo với nhãn minh họa rõ ràng', () => {
    show({ status: 'ready', context }, '/demo')
    expect(screen.getByRole('note').textContent).toContain('Vai trò và dữ liệu trong khu vực này là giả lập')
    expect(screen.getByText('Dashboard minh họa cũ')).toBeTruthy()
    fireEvent.click(screen.getByRole('link', { name: 'Về trang tài khoản thật' }))
    expect(screen.getByRole('heading', { name: 'Trang chủ AURA' })).toBeTruthy()
    expect(screen.queryByText('Dashboard minh họa cũ')).toBeNull()
  })

  it('giữ router-demo trong khu vực có nhãn giả lập', () => {
    show({ status: 'anonymous' }, '/router-demo/admin/dashboard')
    expect(screen.getByRole('note')).toBeTruthy()
    expect(screen.getByText('Router minh họa cũ')).toBeTruthy()
    expect(screen.queryByText('Form upload cá nhân')).toBeNull()
  })

  it('route không tồn tại trả 404 thay vì dùng Dashboard demo làm fallback', () => {
    show({ status: 'ready', context }, '/khong-ton-tai')
    expect(screen.getByRole('heading', { name: /404/ })).toBeTruthy()
    expect(screen.queryByText('Dashboard minh họa cũ')).toBeNull()
  })
})

describe('Nút logout nối session store thật với Firebase giả lập', () => {
  let auth: { currentUser: User | null }
  let observe: (user: User | null) => void

  async function start() {
    const user = { uid: context.firebaseUid } as User
    auth = { currentUser: user }
    mocks.auth.mockReturnValue(auth)
    mocks.observe.mockImplementation((_auth, listener) => {
      observe = listener; listener(user); return vi.fn()
    })
    mocks.load.mockResolvedValue(context)
    render(<BackendSessionProvider>
      <MemoryRouter><ApplicationRoutes /><HistoryProbe /></MemoryRouter>
    </BackendSessionProvider>)
    await screen.findByRole('heading', { name: 'Xin chào, ' + context.displayName })
  }

  it('logout xóa giao diện cũ, về login và chặn Back vào route upload', async () => {
    await start()
    fireEvent.click(screen.getByRole('link', { name: 'Mở trang tải ảnh' }))
    fireEvent.click(screen.getByRole('link', { name: 'Về nhà kiểm thử' }))
    mocks.signOut.mockImplementation(async () => { auth.currentUser = null; observe(null) })
    fireEvent.click(screen.getByRole('button', { name: 'Đăng xuất' }))
    await screen.findByRole('heading', { name: 'AURA — Đăng nhập' })
    expect(mocks.signOut).toHaveBeenCalledExactlyOnceWith(auth)
    expect(screen.queryByText('Bệnh nhân')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Lùi lịch sử kiểm thử' }))
    await waitFor(() => expect(screen.getByTestId('current-route').textContent).toBe('/login'))
    expect(screen.queryByText('Form upload cá nhân')).toBeNull()
    expect(mocks.load).toHaveBeenCalledTimes(1)
  })

  it('logout lỗi thì chặn dữ liệu cũ, hiện lỗi chuẩn và không tự điều hướng', async () => {
    await start()
    mocks.signOut.mockRejectedValueOnce(new Error('private-firebase-error-not-for-ui'))
    fireEvent.click(screen.getByRole('button', { name: 'Đăng xuất' }))
    expect((await screen.findByRole('alert')).textContent).toBe('Không thể đăng xuất. Vui lòng thử lại.')
    expect(screen.getByTestId('current-route').textContent).toBe('/')
    expect(screen.queryByText(context.displayName)).toBeNull()
    expect(screen.queryByText('private-firebase-error-not-for-ui')).toBeNull()
    mocks.signOut.mockImplementation(async () => { auth.currentUser = null; observe(null) })
    fireEvent.click(screen.getByRole('button', { name: 'Đăng xuất / đổi tài khoản' }))
    await screen.findByRole('heading', { name: 'AURA — Đăng nhập' })
  })

  it('logout đang chờ không khôi phục dữ liệu từ observer callback cũ', async () => {
    await start()
    let finish!: () => void
    mocks.signOut.mockReturnValue(new Promise<void>((resolve) => { finish = resolve }))
    fireEvent.click(screen.getByRole('button', { name: 'Đăng xuất' }))
    expect(screen.getByRole('status').textContent).toContain('Đang xác nhận')
    act(() => observe(auth.currentUser))
    expect(screen.queryByText(context.displayName)).toBeNull()
    expect(mocks.load).toHaveBeenCalledTimes(1)
    await act(async () => { auth.currentUser = null; observe(null); finish() })
    await screen.findByRole('heading', { name: 'AURA — Đăng nhập' })
  })
})


describe('Entrypoint main.tsx', () => {
  it('khởi động trang chủ thật với Provider session và router của ứng dụng', async () => {
    const user = { uid: context.firebaseUid } as User
    mocks.auth.mockReturnValue({ currentUser: user })
    mocks.observe.mockImplementation((_auth, listener) => { listener(user); return vi.fn() })
    mocks.load.mockResolvedValue(context)
    window.history.replaceState({}, '', '/')
    vi.stubEnv('VITE_ENABLE_MOCK', 'false')
    mocks.interceptRoot = true
    try {
      await import('../main')
    } finally { mocks.interceptRoot = false; vi.unstubAllEnvs() }
    const application = mocks.rootRender.mock.calls[0]?.[0] as ReactNode
    expect(application).toBeTruthy()
    render(application)
    await screen.findByRole('heading', { name: 'Xin chào, ' + context.displayName })
    expect(screen.getByRole('heading', { name: 'Trang chủ AURA' })).toBeTruthy()
    expect(screen.getByText('Bệnh nhân')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Mở trang tải ảnh' }).getAttribute('href')).toBe('/upload')
    expect(screen.queryByText('Dashboard minh họa cũ')).toBeNull()
  })
})
