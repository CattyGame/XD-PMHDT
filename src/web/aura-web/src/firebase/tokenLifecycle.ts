import { deleteToken } from 'firebase/messaging'
import { registerNotificationDevice } from '../api/notificationDevices'
import { getFirebaseMessaging } from './config'
import { obtainFcmToken } from './messaging'

export async function refreshFcmToken(
  isCurrentSession: () => boolean = () => true,
) {
  const result = await obtainFcmToken()

  if (!isCurrentSession()) {
    return { status: 'cancelled' as const }
  }

  if (result.status !== 'ready') {
    return result
  }

  await registerNotificationDevice(result.token, isCurrentSession)

  if (!isCurrentSession()) {
    return { status: 'cancelled' as const }
  }

  return { status: 'ready' as const }
}

export async function clearFcmOnLogout() {
  const messaging = await getFirebaseMessaging()

  if (!messaging) {
    return { status: 'not-configured' as const }
  }

  try {
    await deleteToken(messaging)

    return { status: 'deleted' as const }
  } catch {
    return { status: 'error' as const }
  }
}
