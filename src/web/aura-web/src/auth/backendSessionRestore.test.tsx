import { StrictMode } from 'react'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Navigate, Route, Routes, useNavigate } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { BackendContext } from '../api/backendContext'
const mocks = vi.hoisted(() => ({ auth: vi.fn(), observe: vi.fn(), signOut: vi.fn(), load: vi.fn(), reload: vi.fn(), unregister: vi.fn() }))
vi.mock('../api/notificationDevices', () => ({ unregisterNotificationDevice: mocks.unregister }))
vi.mock('../firebase/config', () => ({ getFirebaseAuth: mocks.auth }))
vi.mock('firebase/auth', () => ({ onAuthStateChanged: mocks.observe, signOut: mocks.signOut }))
vi.mock('../api/backendContext', async (original) => ({
  ...await original<typeof import('../api/backendContext')>(), loadBackendContext: mocks.load,
}))
vi.mock('./sessionPageLifecycle', async (original) => {
  const actual = await original<typeof import('./sessionPageLifecycle')>()
  return { ...actual, bindSessionPageLifecycle: (options: Parameters<typeof actual.bindSessionPageLifecycle>[0]) =>
    actual.bindSessionPageLifecycle({ ...options, reload: mocks.reload }) }
})
import BackendSessionProvider from './BackendSessionProvider'
import { useBackendSession } from './backendSession'
import { createBackendSessionStore } from './backendSessionStore'
import ProtectedRoute from '../routes/ProtectedRoute'
const user = { uid: 'synthetic-restore-user' }
const auth: { currentUser: typeof user | null } = { currentUser: user }
const context: BackendContext = {
  firebaseUid: user.uid, accountId: '11111111-1111-4111-8111-111111111111', patientId: '22222222-2222-4222-8222-222222222222',
  personalTenantId: '33333333-3333-4333-8333-333333333333', roles: ['PATIENT'], displayName: 'Hồ sơ cũ kiểm thử', profileVersion: 1,
}
let callbacks: Array<(value: typeof auth.currentUser) => void>
let roots: HTMLElement[]
function Login() {
  const { state } = useBackendSession()
  return state.status === 'ready' ? <Navigate to="/" replace /> : <p>LOGIN ANONYMOUS</p>
}
function Panel() {
  const { state, logout } = useBackendSession()
  return state.status === 'ready' ? <><p>{state.context.displayName}</p><button onClick={() => void logout()}>Logout test</button></> : null
}
function Back() { const navigate = useNavigate(); return <button onClick={() => navigate(-1)}>Back test</button> }
function app(strict = false, history = ['/upload']) {
  const root = document.createElement('div'); root.id = 'root'; document.body.append(root); roots.push(root)
  const tree = <BackendSessionProvider><MemoryRouter initialEntries={history}><Back /><Routes>
    <Route path="/login" element={<Login />} />
    <Route path="/" element={<ProtectedRoute><Panel /></ProtectedRoute>} />
    <Route path="/upload" element={<ProtectedRoute><Panel /></ProtectedRoute>} />
  </Routes></MemoryRouter></BackendSessionProvider>
  return { root, ...render(strict ? <StrictMode>{tree}</StrictMode> : tree, { container: root }) }
}
function transition(name: 'pagehide' | 'pageshow', persisted = true) {
  act(() => { window.dispatchEvent(new PageTransitionEvent(name, { persisted })) })
}
beforeEach(() => {
  vi.resetAllMocks(); callbacks = []; roots = []; auth.currentUser = user
  mocks.auth.mockReturnValue(auth); mocks.load.mockResolvedValue(context)
  mocks.observe.mockImplementation((_auth, next) => {
    callbacks.push(next); let observing = true
    queueMicrotask(() => { if (observing) next(auth.currentUser) })
    return () => { observing = false }
  })
  mocks.signOut.mockImplementation(async () => { auth.currentUser = null; for (const next of callbacks) next(null) })
})
afterEach(() => { cleanup(); for (const root of roots) root.remove() })
describe('Document restore must not revive a stale session', () => {
  it('does not sign out a newer account after old device cleanup finishes late', async () => {
    const store=createBackendSessionStore();const stop=store.start()
    await waitFor(() => expect(store.getSnapshot().status).toBe('ready'))
    let finish: () => void = () => {}
    mocks.unregister.mockImplementation(() => new Promise<void>(resolve => { finish=resolve }))
    const pending=store.logout()
    const other={ uid:'newer-user' };auth.currentUser=other
    for(const next of callbacks) next(other)
    finish();await pending
    expect(mocks.signOut).not.toHaveBeenCalled();expect(auth.currentUser).toBe(other)
    stop()
  })
  it('conceals before caching and removes the old profile without signing out', async () => {
    const { root } = app(); await screen.findByText(context.displayName); transition('pagehide')
    expect(root.hidden).toBe(true); expect(root.hasAttribute('inert')).toBe(true)
    expect(root.style.getPropertyValue('visibility')).toBe('hidden'); expect(root.style.getPropertyPriority('visibility')).toBe('important')
    expect(screen.queryByText(context.displayName)).toBeNull(); expect(mocks.load).toHaveBeenCalledTimes(1)
    expect(mocks.signOut).not.toHaveBeenCalled(); expect(auth.currentUser).toBe(user)
  })
  it('suspends even if pagehide does not promise a cache entry', async () => {
    const { root } = app(); await screen.findByText(context.displayName); transition('pagehide', false)
    for (const next of callbacks) act(() => { next(user) })
    expect(root.hidden).toBe(true); expect(screen.queryByText(context.displayName)).toBeNull(); expect(mocks.load).toHaveBeenCalledTimes(1)
  })
  it('reloads only once on persisted restore and sends no context request from the old document', async () => {
    const { root } = app(); await screen.findByText(context.displayName); transition('pagehide'); transition('pageshow'); transition('pageshow')
    expect(mocks.reload).toHaveBeenCalledTimes(1); expect(mocks.load).toHaveBeenCalledTimes(1); expect(root.hidden).toBe(true)
    expect(screen.queryByText(context.displayName)).toBeNull()
  })
  it('also conceals and suspends a persisted restore without an observed pagehide', async () => {
    const { root } = app(); await screen.findByText(context.displayName); transition('pageshow')
    expect(root.hidden).toBe(true); expect(screen.queryByText(context.displayName)).toBeNull()
    expect(mocks.reload).toHaveBeenCalledTimes(1); expect(mocks.load).toHaveBeenCalledTimes(1)
  })
  it('leaves a normal pageshow and Google popup focus/visibility changes alone', async () => {
    const { root } = app(); await screen.findByText(context.displayName); transition('pageshow', false)
    fireEvent.blur(window); fireEvent(document, new Event('visibilitychange'))
    expect(root.hidden).toBe(false); expect(screen.getByText(context.displayName)).toBeTruthy()
    expect(mocks.reload).not.toHaveBeenCalled(); expect(mocks.load).toHaveBeenCalledTimes(1)
  })
  it.each(['success', 'failure'])('aborts a pending context load and ignores late %s after pagehide', async (result) => {
    let resolve!: (value: BackendContext) => void; let reject!: (reason: Error) => void
    mocks.load.mockReturnValue(new Promise<BackendContext>((done, fail) => { resolve = done; reject = fail }))
    app(); await waitFor(() => expect(mocks.load).toHaveBeenCalledTimes(1))
    const signal = mocks.load.mock.calls[0][0] as AbortSignal; transition('pagehide'); expect(signal.aborted).toBe(true)
    await act(async () => { if (result === 'success') resolve(context); else reject(new Error('late HTTP failure')) })
    transition('pageshow'); expect(screen.queryByText(context.displayName)).toBeNull(); expect(mocks.load).toHaveBeenCalledTimes(1)
  })
  it('cannot restart a suspended store through retry, logout or queued Firebase callbacks', async () => {
    const store = createBackendSessionStore(); const stop = store.start(); await act(async () => { await Promise.resolve() })
    expect(store.getSnapshot().status).toBe('ready'); store.suspendForNavigation(); store.start(); store.retry(); await store.logout()
    for (const next of callbacks) next(user)
    expect(store.getSnapshot()).toEqual({ status: 'loading', firebaseUid: null }); expect(mocks.load).toHaveBeenCalledTimes(1)
    expect(mocks.observe).toHaveBeenCalledTimes(1); expect(mocks.signOut).not.toHaveBeenCalled(); stop()
  })
  it('a fresh document after logout reads anonymous persistence without bootstrapping', async () => {
    const old = app(); await screen.findByText(context.displayName); transition('pagehide'); auth.currentUser = null; transition('pageshow')
    old.unmount(); old.root.remove(); const fresh = app(); await screen.findByText('LOGIN ANONYMOUS')
    expect(fresh.root.hidden).toBe(false); expect(screen.queryByText(context.displayName)).toBeNull(); expect(mocks.load).toHaveBeenCalledTimes(1)
  })
  it('same-document Back after logout remains protected without requiring a reload', async () => {
    app(false, ['/upload', '/']); await screen.findByText(context.displayName)
    fireEvent.click(screen.getByRole('button', { name: 'Logout test' })); await screen.findByText('LOGIN ANONYMOUS')
    fireEvent.click(screen.getByRole('button', { name: 'Back test' })); await screen.findByText('LOGIN ANONYMOUS')
    expect(screen.queryByText(context.displayName)).toBeNull(); expect(mocks.load).toHaveBeenCalledTimes(1); expect(mocks.reload).not.toHaveBeenCalled()
  })
  it('cleans up StrictMode listeners and blocks callbacks after unmount', async () => {
    const rendered = app(true); await screen.findByText(context.displayName); transition('pagehide'); transition('pageshow')
    expect(mocks.reload).toHaveBeenCalledTimes(1); rendered.unmount(); transition('pageshow')
    for (const next of callbacks) next(user)
    expect(mocks.reload).toHaveBeenCalledTimes(1); expect(mocks.load).toHaveBeenCalledTimes(1)
  })
})
