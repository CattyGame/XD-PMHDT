import { getToken, onMessage, type MessagePayload } from 'firebase/messaging'
import { getFirebaseMessaging } from './config'
import { registerFirebaseServiceWorker } from './serviceWorker'

export type FcmTokenResult =
  | { status: 'ready'; token: string }
  | { status: 'not-configured' }
  | { status: 'permission-required' }
  | { status: 'unsupported' }
  | { status: 'error'; message: string }

export async function obtainFcmToken(): Promise<FcmTokenResult> {
  if (!window.isSecureContext || !('serviceWorker' in navigator)) return { status: 'unsupported' }
  if (!('Notification' in window)) return { status: 'unsupported' }
  if (Notification.permission !== 'granted') return { status: 'permission-required' }
  if (!import.meta.env.VITE_FIREBASE_VAPID_KEY) return { status: 'not-configured' }
  try {
    const messaging = await getFirebaseMessaging()
    if (!messaging) return { status: 'not-configured' }
    const registration = await registerFirebaseServiceWorker()
    if (!registration) return { status: 'error', message: 'Không thể đăng ký Firebase Service Worker.' }
    const token = await getToken(messaging, {
      vapidKey: import.meta.env.VITE_FIREBASE_VAPID_KEY,
      serviceWorkerRegistration: registration,
    })
    if (!token) return { status: 'error', message: 'Firebase không trả về FCM token.' }
    return { status: 'ready', token }
  } catch (error) {
    return { status: 'error', message: error instanceof Error ? error.message : 'Lỗi FCM không xác định.' }
  }
}

// Một onMessage Firebase duy nhất, phát cho các subscriber trong app (toast, màn kiểm thử).
const listeners = new Set<(payload: MessagePayload) => void>()
let stopFirebase: (() => void) | null = null
let starting: Promise<boolean> | null = null

async function ensureListening(): Promise<boolean> {
  if (stopFirebase) return true
  if (!starting) {
    starting = (async () => {
      const messaging = await getFirebaseMessaging()
      if (!messaging) return false
      stopFirebase = onMessage(messaging, payload => {
        for (const listener of listeners) listener(payload)
      })
      return true
    })().finally(() => { starting = null })
  }
  return starting
}

export async function listenForegroundMessages(
  callback: (payload: MessagePayload) => void,
): Promise<(() => void) | null> {
  listeners.add(callback)
  let ready: boolean
  try { ready = await ensureListening() }
  catch (error) { listeners.delete(callback); throw error }
  if (!ready) { listeners.delete(callback); return null }
  let active = true
  const unsubscribe = () => {
    if (!active) return
    active = false
    listeners.delete(callback)
    if (listeners.size === 0) {
      stopFirebase?.()
      stopFirebase = null
    }
  }
  if (!listeners.has(callback)) { unsubscribe(); return null }
  return unsubscribe
}
