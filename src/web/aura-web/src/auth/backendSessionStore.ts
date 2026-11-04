import { onAuthStateChanged, signOut, type User } from 'firebase/auth'
import { getFirebaseAuth } from '../firebase/config'
import { unregisterNotificationDevice } from '../api/notificationDevices'
import { clearFcmOnLogout } from '../firebase/tokenLifecycle'
import { BackendContextError, loadBackendContext, type BackendContext } from '../api/backendContext'

export type BackendSessionState =
  | { status: 'loading'; firebaseUid: string | null }
  | { status: 'anonymous' }
  | { status: 'ready'; context: BackendContext }
  | { status: 'error'; firebaseUid: string | null; error: BackendContextError }

/** One store per provider. No persistent context or Firebase token. */
export function createBackendSessionStore() {
  let state: BackendSessionState = { status: 'loading', firebaseUid: null }
  const listeners = new Set<() => void>()
  let auth: ReturnType<typeof getFirebaseAuth> = null
  let currentUser: User | null = null
  let unsubscribe: (() => void) | undefined
  let controller: AbortController | undefined
  let generation = 0
  let active = false
  let navigationSuspended = false
  let signOutOperation: { user: User | null } | undefined

  function publish(next: BackendSessionState) {
    state = next
    for (const listener of listeners) listener()
  }

  function invalidate() {
    generation += 1
    controller?.abort()
    controller = undefined
  }

  function load(user: User) {
    invalidate()
    const run = generation
    const abort = new AbortController()
    controller = abort
    currentUser = user
    publish({ status: 'loading', firebaseUid: user.uid })
    const stillCurrent = () => active && run === generation && !abort.signal.aborted &&
      auth?.currentUser === user && currentUser === user

    void loadBackendContext(abort.signal).then((context) => {
      if (!stillCurrent()) return
      if (context.firebaseUid !== user.uid) {
        publish({ status: 'error', firebaseUid: user.uid,
          error: new BackendContextError(401, 'ERR_SESSION_CHANGED') })
        return
      }
      publish({ status: 'ready', context })
    }).catch((error: unknown) => {
      if (!stillCurrent()) return
      publish({ status: 'error', firebaseUid: user.uid,
        error: error instanceof BackendContextError
          ? error : new BackendContextError(0, 'ERR_SESSION_CHECK_FAILED', true) })
    })
  }

  function stop() {
    active = false
    invalidate()
    unsubscribe?.()
    unsubscribe = undefined
    currentUser = null
    signOutOperation = undefined
    publish({ status: 'loading', firebaseUid: null })
  }

  function start() {
    if (navigationSuspended) return stop
    if (active) return stop
    active = true
    auth = getFirebaseAuth()
    if (!auth) {
      publish({ status: 'error', firebaseUid: null,
        error: new BackendContextError(0, 'ERR_FIREBASE_NOT_CONFIGURED') })
      return stop
    }
    publish({ status: 'loading', firebaseUid: null })
    // Exclude callbacks queued by an observer that has already been disposed.
    let observing = true
    const detach = onAuthStateChanged(auth, (user) => {
      if (!active || !observing) return
      if (!user) {
        invalidate()
        currentUser = null
        signOutOperation = undefined
        publish({ status: 'anonymous' })
      } else if (auth?.currentUser === user) {
        if (signOutOperation?.user === user) return
        signOutOperation = undefined
        if (currentUser === user) return
        load(user)
      }
    }, () => {
      if (!active || !observing) return
      invalidate()
      currentUser = null
      publish({ status: 'error', firebaseUid: auth?.currentUser?.uid ?? null,
        error: new BackendContextError(0, 'ERR_AUTH_OBSERVER', true) })
    })
    unsubscribe = () => { observing = false; detach() }
    return stop
  }

  function retry() {
    if (!active || signOutOperation || state.status !== 'error' || !state.error.retryable ||
      state.error.status === 401 || state.error.status === 403) return
    if (state.error.code === 'ERR_AUTH_OBSERVER') {
      stop()
      start()
    } else if (auth?.currentUser) {
      load(auth.currentUser)
    }
  }

  async function logout() {
    if (!active || signOutOperation || !auth) return
    const operation = { user: auth.currentUser }
    signOutOperation = operation
    invalidate()
    currentUser = null
    publish({ status: 'loading', firebaseUid: auth.currentUser?.uid ?? null })
    try {
      // Hủy liên kết thiết bị trước khi kết thúc phiên có token hợp lệ.
      // Thao tác lỗi không được giữ người dùng đăng nhập vô thời hạn.
      try { await unregisterNotificationDevice() } catch { /* Best effort; backend có thể tạm lỗi. */ }
      try { await clearFcmOnLogout() } catch { /* Firebase có thể chưa được cấu hình. */ }
      await signOut(auth)
      if (!active || signOutOperation !== operation) return
      if (!auth.currentUser) publish({ status: 'anonymous' })
    } catch {
      if (!active || signOutOperation !== operation) return
      publish({ status: 'error', firebaseUid: auth.currentUser?.uid ?? null,
        error: new BackendContextError(0, 'ERR_SIGN_OUT_FAILED') })
    } finally {
      if (signOutOperation === operation) signOutOperation = undefined
    }
  }

  function suspendForNavigation() {
    navigationSuspended = true
    stop()
  }

  return {
    getSnapshot: () => state,
    subscribe: (listener: () => void) => {
      listeners.add(listener)
      return () => { listeners.delete(listener) }
    },
    start,
    retry,
    logout,
    suspendForNavigation,
  }
}
