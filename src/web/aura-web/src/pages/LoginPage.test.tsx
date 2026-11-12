import { StrictMode } from 'react'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  getAuth: vi.fn(), observe: vi.fn(), load: vi.fn(), email: vi.fn(), google: vi.fn(),
  persistence: vi.fn(), signOut: vi.fn(),
}))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.getAuth }))
vi.mock('firebase/auth', () => ({
  onAuthStateChanged: mocks.observe, signOut: mocks.signOut,
  signInWithEmailAndPassword: mocks.email, signInWithPopup: mocks.google,
  setPersistence: mocks.persistence, browserSessionPersistence: 'session',
  GoogleAuthProvider: class {},
}))
vi.mock('../api/backendContext', async (original) => ({
  ...await original<typeof import('../api/backendContext')>(), loadBackendContext: mocks.load,
}))
import { BackendContextError, type BackendContext } from '../api/backendContext'
import BackendSessionProvider from '../auth/BackendSessionProvider'
import { useBackendSession } from '../auth/backendSession'
import ProtectedRoute from '../routes/ProtectedRoute'
import LoginPage from './LoginPage'

type User = { uid: string }
type Observer = { next: (user: User | null) => void; error: () => void; active: boolean }
const userA = { uid: 'firebase-a' }
const userB = { uid: 'firebase-b' }
const auth: { currentUser: User | null } = { currentUser: null }
let observers: Observer[] = []
const contextA: BackendContext = {
  firebaseUid: userA.uid, accountId: '10000000-0000-0000-0000-000000000001', roles: ['PATIENT'],
  patientId: '20000000-0000-0000-0000-000000000002',
  personalTenantId: '30000000-0000-0000-0000-000000000003', displayName: 'Người A', profileVersion: 1,
}
const contextB = { ...contextA, firebaseUid: userB.uid, displayName: 'Người B' }

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

function publishUser(user: User | null) {
  auth.currentUser = user
  for (const observer of observers) if (observer.active) observer.next(user)
}
async function emit(user: User | null) { await act(async () => { publishUser(user) }) }
function Content() {
  const { state, logout } = useBackendSession()
  return <section><p>Nội dung được bảo vệ</p>
    {state.status === 'ready' && <p>{state.context.displayName}</p>}
    <button onClick={() => void logout()}>Đăng xuất trong ứng dụng</button>
  </section>
}
function app(path = '/login', strict = false) {
  const tree = <BackendSessionProvider><MemoryRouter initialEntries={[path]}><Routes>
    <Route path="/login" element={<LoginPage />} />
    <Route path="/" element={<ProtectedRoute><Content /></ProtectedRoute>} />
    <Route path="/upload" element={<ProtectedRoute><Content /></ProtectedRoute>} />
  </Routes></MemoryRouter></BackendSessionProvider>
  return render(strict ? <StrictMode>{tree}</StrictMode> : tree)
}
async function fillEmail() {
  fireEvent.change(await screen.findByLabelText('Email'), { target: { value: 'a@example.test' } })
  fireEvent.change(screen.getByLabelText('Mật khẩu'), { target: { value: 'test-password' } })
}

beforeEach(() => {
  vi.resetAllMocks()
  auth.currentUser = null
  observers = []
  mocks.getAuth.mockReturnValue(auth)
  mocks.load.mockResolvedValue(contextA)
  mocks.persistence.mockResolvedValue(undefined)
  mocks.email.mockResolvedValue({ user: userA })
  mocks.google.mockResolvedValue({ user: userA })
  mocks.signOut.mockImplementation(async () => { publishUser(null) })
  mocks.observe.mockImplementation((_auth, next, error) => {
    const observer = { next, error, active: true }
    observers.push(observer)
    queueMicrotask(() => { if (observer.active) next(auth.currentUser) })
    return () => { observer.active = false }
  })
})
afterEach(cleanup)

describe('M5 session-aware login and protected routes', () => {
  it('masks the password and toggles visibility without starting authentication', async () => {
    app()
    await fillEmail()
    const input = screen.getByLabelText('Mật khẩu') as HTMLInputElement
    expect(input.type).toBe('password')
    fireEvent.click(screen.getByRole('button', { name: 'Hiện mật khẩu' }))
    expect(input.type).toBe('text')
    expect(input.value).toBe('test-password')
    expect(screen.getByRole('button', { name: 'Ẩn mật khẩu' }).getAttribute('aria-pressed')).toBe('true')
    fireEvent.click(screen.getByRole('button', { name: 'Ẩn mật khẩu' }))
    expect(input.type).toBe('password')
    expect(mocks.email).not.toHaveBeenCalled()
    expect(mocks.google).not.toHaveBeenCalled()
    expect(mocks.load).not.toHaveBeenCalled()
  })

  it('locks password visibility during sign-in and clears and masks it after failure', async () => {
    const pending = deferred<{ user: User }>()
    mocks.email.mockReturnValue(pending.promise)
    app()
    await fillEmail()
    fireEvent.click(screen.getByRole('button', { name: 'Hiện mật khẩu' }))
    fireEvent.click(screen.getByRole('button', { name: 'Đăng nhập bằng Email' }))
    await waitFor(() => expect(mocks.email).toHaveBeenCalledTimes(1))
    expect(screen.getByRole('button', { name: 'Ẩn mật khẩu' })).toHaveProperty('disabled', true)
    await act(async () => pending.reject(new Error('synthetic-provider-detail')))
    expect((await screen.findByRole('alert')).textContent).not.toContain('synthetic-provider-detail')
    const input = screen.getByLabelText('Mật khẩu') as HTMLInputElement
    expect(input.value).toBe('')
    expect(input.type).toBe('password')
    expect(screen.getByRole('button', { name: 'Hiện mật khẩu' })).toHaveProperty('disabled', false)
    expect(mocks.load).not.toHaveBeenCalled()
  })

  it('shows email and Google sign-in controls after the anonymous session is known', async () => {
    app()
    expect(await screen.findByLabelText('Email')).toBeTruthy()
    expect(screen.getByLabelText('Mật khẩu')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Đăng nhập bằng Email' })).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Tiếp tục với Google' })).toBeTruthy()
    expect(mocks.load).not.toHaveBeenCalled()
  })

  it('shows a configuration error without trying Firebase sign-in', async () => {
    mocks.getAuth.mockReturnValue(null)
    app()
    expect((await screen.findByRole('alert')).textContent).toContain('chưa được cấu hình')
    expect(screen.queryByLabelText('Email')).toBeNull()
    expect(mocks.observe).not.toHaveBeenCalled()
    expect(mocks.email).not.toHaveBeenCalled()
    expect(mocks.load).not.toHaveBeenCalled()
  })

  it.each(['/', '/upload'])('redirects anonymous direct visits from %s to login', async (path) => {
    app(path)
    expect(await screen.findByLabelText('Email')).toBeTruthy()
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
  })

  it('holds an existing Firebase session at login until backend context is ready', async () => {
    auth.currentUser = userA
    const pending = deferred<BackendContext>()
    mocks.load.mockReturnValue(pending.promise)
    app()
    expect(await screen.findByRole('status')).toBeTruthy()
    await waitFor(() => expect(mocks.load).toHaveBeenCalledTimes(1))
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
    await act(async () => pending.resolve(contextA))
    expect(await screen.findByText('Nội dung được bảo vệ')).toBeTruthy()
    expect(screen.getByText('Người A')).toBeTruthy()
  })

  it.each(['/', '/upload'])('holds the protected route %s while backend context is loading', async (path) => {
    auth.currentUser = userA
    mocks.load.mockReturnValue(new Promise(() => {}))
    app(path)
    await waitFor(() => expect(mocks.load).toHaveBeenCalledTimes(1))
    expect(screen.getByRole('status')).toBeTruthy()
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
  })

  it('does not navigate just because the email sign-in promise resolved', async () => {
    app()
    await fillEmail()
    fireEvent.click(screen.getByRole('button', { name: 'Đăng nhập bằng Email' }))
    await waitFor(() => expect(mocks.email).toHaveBeenCalledWith(auth, 'a@example.test', 'test-password'))
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
    expect(mocks.load).not.toHaveBeenCalled()
    await waitFor(() => expect((screen.getByLabelText('Mật khẩu') as HTMLInputElement).value).toBe(''))
  })

  it.each(['email', 'google'])('loads backend context after %s authentication before entering the app', async (method) => {
    const pending = deferred<BackendContext>()
    mocks.load.mockReturnValue(pending.promise)
    const signIn = method === 'email' ? mocks.email : mocks.google
    signIn.mockImplementation(async () => { publishUser(userA); return { user: userA } })
    app()
    if (method === 'email') await fillEmail()
    else await screen.findByRole('button', { name: 'Tiếp tục với Google' })
    fireEvent.click(screen.getByRole('button', { name: method === 'email' ? 'Đăng nhập bằng Email' : 'Tiếp tục với Google' }))
    await waitFor(() => expect(mocks.load).toHaveBeenCalledTimes(1))
    expect(mocks.persistence).toHaveBeenCalledWith(auth, 'session')
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
    await act(async () => pending.resolve(contextA))
    expect(await screen.findByText('Nội dung được bảo vệ')).toBeTruthy()
    expect(mocks.load).toHaveBeenCalledTimes(1)
  })

  it.each(['email', 'google'])('shows safe %s sign-in failure and clears the password', async (method) => {
    const signIn = method === 'email' ? mocks.email : mocks.google
    signIn.mockRejectedValue(new Error('provider-secret-details'))
    app()
    await fillEmail()
    fireEvent.click(screen.getByRole('button', { name: method === 'email' ? 'Đăng nhập bằng Email' : 'Tiếp tục với Google' }))
    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain(method === 'email' ? 'Đăng nhập thất bại' : 'Không thể đăng nhập Google')
    expect(alert.textContent).not.toContain('provider-secret-details')
    expect((screen.getByLabelText('Mật khẩu') as HTMLInputElement).value).toBe('')
    expect(mocks.load).not.toHaveBeenCalled()
  })

  it('prevents duplicate submit while Firebase sign-in is pending', async () => {
    const pending = deferred<{ user: User }>()
    mocks.email.mockReturnValue(pending.promise)
    app()
    await fillEmail()
    const button = screen.getByRole('button', { name: 'Đăng nhập bằng Email' })
    fireEvent.click(button)
    fireEvent.click(button)
    await waitFor(() => expect(mocks.email).toHaveBeenCalledTimes(1))
    expect((screen.getByRole('button', { name: 'Tiếp tục với Google' }) as HTMLButtonElement).disabled).toBe(true)
    await act(async () => pending.resolve({ user: userA }))
  })

  it('keeps PENDING out of the app and retries only on an explicit click', async () => {
    auth.currentUser = userA
    mocks.load.mockRejectedValueOnce(new BackendContextError(409, 'ERR_PROFILE_NOT_READY', true))
    app()
    expect((await screen.findByRole('alert')).textContent).toContain('đang được khởi tạo')
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
    await emit(userA)
    expect(mocks.load).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Thử lại xác nhận hồ sơ' }))
    expect(await screen.findByText('Nội dung được bảo vệ')).toBeTruthy()
    expect(mocks.load).toHaveBeenCalledTimes(2)
  })

  it.each([
    [401, 'ERR_UNAUTHORIZED', 'Phiên đăng nhập'],
    [403, 'ERR_ACCOUNT_DISABLED', 'đã bị khóa'],
    [403, 'ERR_FORBIDDEN', 'chưa được phép'],
    [0, 'ERR_INVALID_RESPONSE', 'không hợp lệ'],
    [0, 'ERR_MOCK_ENABLED', 'chế độ demo'],
  ])('blocks context error %i/%s without an automatic retry', async (status, code, text) => {
    auth.currentUser = userA
    mocks.load.mockRejectedValue(new BackendContextError(status as number, code as string))
    app('/')
    expect((await screen.findByRole('alert')).textContent).toContain(text)
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Thử lại xác nhận hồ sơ' })).toBeNull()
    expect(mocks.load).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Đăng xuất / đổi tài khoản' })).toBeTruthy()
  })

  it('shows a correlation reference for transient errors and supports manual recovery', async () => {
    auth.currentUser = userA
    mocks.load.mockRejectedValueOnce(new BackendContextError(503, 'ERR_UNAVAILABLE', true, 'request-123'))
    app('/')
    expect(await screen.findByText('Mã đối chiếu: request-123')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Thử lại xác nhận hồ sơ' }))
    expect(await screen.findByText('Nội dung được bảo vệ')).toBeTruthy()
  })

  it('discards a late success after logout and aborts the old context load', async () => {
    auth.currentUser = userA
    const pending = deferred<BackendContext>()
    mocks.load.mockReturnValue(pending.promise)
    app('/')
    await waitFor(() => expect(mocks.load).toHaveBeenCalledTimes(1))
    const signal = mocks.load.mock.calls[0][0] as AbortSignal
    await emit(null)
    expect(signal.aborted).toBe(true)
    await act(async () => pending.resolve(contextA))
    expect(await screen.findByLabelText('Email')).toBeTruthy()
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
  })

  it('replaces ready context on account switch and ignores the old completion', async () => {
    auth.currentUser = userA
    const old = deferred<BackendContext>()
    const next = deferred<BackendContext>()
    mocks.load.mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise)
    app('/')
    await waitFor(() => expect(mocks.load).toHaveBeenCalledTimes(1))
    await emit(userB)
    expect(mocks.load).toHaveBeenCalledTimes(2)
    await act(async () => old.resolve(contextA))
    expect(screen.queryByText('Người A')).toBeNull()
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
    await act(async () => next.resolve(contextB))
    expect(await screen.findByText('Người B')).toBeTruthy()
  })

  it('unmounts protected children immediately when a ready account changes', async () => {
    auth.currentUser = userA
    app('/')
    expect(await screen.findByText('Người A')).toBeTruthy()
    const next = deferred<BackendContext>()
    mocks.load.mockReturnValueOnce(next.promise)
    await emit(userB)
    expect(screen.queryByText('Người A')).toBeNull()
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
    await act(async () => next.resolve(contextB))
    expect(await screen.findByText('Người B')).toBeTruthy()
  })

  it('does not bootstrap twice for duplicate auth events with the same User', async () => {
    auth.currentUser = userA
    app('/')
    expect(await screen.findByText('Người A')).toBeTruthy()
    await emit(userA)
    expect(mocks.load).toHaveBeenCalledTimes(1)
  })

  it('supports StrictMode effect restart without accepting a disposed observer', async () => {
    auth.currentUser = userA
    app('/', true)
    expect(await screen.findByText('Người A')).toBeTruthy()
    expect(observers).toHaveLength(2)
    expect(observers.filter((observer) => observer.active)).toHaveLength(1)
    expect(mocks.load).toHaveBeenCalledTimes(1)
    await act(async () => { observers[0].next(null) })
    expect(screen.getByText('Người A')).toBeTruthy()
  })

  it('aborts pending work when the provider unmounts', async () => {
    auth.currentUser = userA
    const pending = deferred<BackendContext>()
    mocks.load.mockReturnValue(pending.promise)
    const view = app('/')
    await waitFor(() => expect(mocks.load).toHaveBeenCalledTimes(1))
    const signal = mocks.load.mock.calls[0][0] as AbortSignal
    view.unmount()
    expect(signal.aborted).toBe(true)
    expect(observers.every((observer) => !observer.active)).toBe(true)
    await act(async () => pending.resolve(contextA))
  })

  it('handles an auth observer error without revealing provider diagnostics', async () => {
    auth.currentUser = userA
    app('/')
    expect(await screen.findByText('Người A')).toBeTruthy()
    await act(async () => { observers.find((observer) => observer.active)?.error() })
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Thử lại xác nhận hồ sơ' }))
    expect(await screen.findByText('Người A')).toBeTruthy()
    expect(mocks.load).toHaveBeenCalledTimes(2)
  })

  it('allows switching out of a disabled account into the sign-in form', async () => {
    auth.currentUser = userA
    mocks.load.mockRejectedValue(new BackendContextError(403, 'ERR_ACCOUNT_DISABLED'))
    app('/')
    fireEvent.click(await screen.findByRole('button', { name: 'Đăng xuất / đổi tài khoản' }))
    expect(await screen.findByLabelText('Email')).toBeTruthy()
    expect(mocks.signOut).toHaveBeenCalledTimes(1)
  })

  it('does not restore protected content if sign-out fails', async () => {
    auth.currentUser = userA
    mocks.signOut.mockRejectedValue(new Error('private-signout-details'))
    app('/')
    fireEvent.click(await screen.findByRole('button', { name: 'Đăng xuất trong ứng dụng' }))
    expect((await screen.findByRole('alert')).textContent).toBe('Không thể đăng xuất. Vui lòng thử lại.')
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
    expect(mocks.load).toHaveBeenCalledTimes(1)
  })

  it('rejects a context returned for a different Firebase uid', async () => {
    auth.currentUser = userA
    mocks.load.mockResolvedValue(contextB)
    app('/')
    expect((await screen.findByRole('alert')).textContent).toContain('Phiên đăng nhập')
    expect(screen.queryByText('Nội dung được bảo vệ')).toBeNull()
  })

  it.each([userA, userB])('does not overwrite a new login of %j with completion of an older sign-out', async (nextUser) => {
    auth.currentUser = userA
    const oldSignOut = deferred<void>()
    mocks.signOut.mockImplementation(() => { publishUser(null); return oldSignOut.promise })
    app('/')
    fireEvent.click(await screen.findByRole('button', { name: 'Đăng xuất trong ứng dụng' }))
    expect(await screen.findByLabelText('Email')).toBeTruthy()
    mocks.load.mockResolvedValue({ ...contextA, firebaseUid: nextUser.uid, displayName: 'Phiên mới' })
    await emit(nextUser)
    expect(await screen.findByText('Phiên mới')).toBeTruthy()
    await act(async () => oldSignOut.reject(new Error('late failure from previous session')))
    expect(screen.getByText('Phiên mới')).toBeTruthy()
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('maps unexpected loader exceptions to a safe retryable failure', async () => {
    auth.currentUser = userA
    mocks.load.mockRejectedValue(new Error('private-runtime-details'))
    app('/')
    expect((await screen.findByRole('alert')).textContent).not.toContain('private-runtime-details')
    expect(screen.getByRole('button', { name: 'Thử lại xác nhận hồ sơ' })).toBeTruthy()
  })
})
