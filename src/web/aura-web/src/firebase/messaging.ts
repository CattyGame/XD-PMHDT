
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
  if (!window.isSecureContext || !('serviceWorker' in navigator)) {
    return { status: 'unsupported' }
  }

  if (!('Notification' in window)) {
    return { status: 'unsupported' }
  }

  if (Notification.permission !== 'granted') {
    return { status: 'permission-required' }
  }

  if (!import.meta.env.VITE_FIREBASE_VAPID_KEY) {
    return { status: 'not-configured' }
  }

  try {
    const messaging = await getFirebaseMessaging()

    if (!messaging) {
      return { status: 'not-configured' }
    }

    const registration = await registerFirebaseServiceWorker()

    if (!registration) {
      return {
        status: 'error',
        message: 'Không thể đăng ký Firebase Service Worker.',
      }
    }

    const token = await getToken(messaging, {
      vapidKey: import.meta.env.VITE_FIREBASE_VAPID_KEY,
      serviceWorkerRegistration: registration,
    })

    if (!token) {
      return {
        status: 'error',
        message: 'Firebase không trả về FCM token.',
      }
    }

    return { status: 'ready', token }
  } catch (error) {
    return {
      status: 'error',
      message: error instanceof Error ? error.message : 'Lỗi FCM không xác định.',
    }
  }
}

export async function listenForegroundMessages(
  callback: (payload: MessagePayload) => void
): Promise<(() => void) | null> {
  const messaging = await getFirebaseMessaging()

  if (!messaging) return null

  return onMessage(messaging, callback)
}
