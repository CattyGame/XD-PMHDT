import { isMockEnabled } from './client'
import { getNotificationIdToken } from '../firebase/auth'

const deviceStorageKey =
  `aura.notification.device-id.v1:${import.meta.env.VITE_FIREBASE_PROJECT_ID}`

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export class NotificationDeviceError extends Error {
  readonly status: number
  readonly code: string

  constructor(status: number, code: string) {
    super('Không hoàn thành được yêu cầu đăng ký thiết bị.')
    this.name = 'NotificationDeviceError'
    this.status = status
    this.code = code
  }
}

function getSavedDeviceId(): string | null {
  const saved = localStorage.getItem(deviceStorageKey)

  if (!saved || !uuidPattern.test(saved)) {
    return null
  }

  return saved
}

export function getOrCreateDeviceId(): string {
  const saved = getSavedDeviceId()

  if (saved) {
    return saved
  }

  const deviceId = crypto.randomUUID()
  localStorage.setItem(deviceStorageKey, deviceId)

  return deviceId
}

async function sendDeviceRequest(
  method: 'POST' | 'DELETE',
  path: string,
  body?: { deviceId: string; token: string },
  isCurrentSession: () => boolean = () => true,
): Promise<Response> {
  if (isMockEnabled) {
    throw new NotificationDeviceError(0, 'ERR_MOCK_ENABLED')
  }

  const idToken = await getNotificationIdToken()
  if (!isCurrentSession()) throw new NotificationDeviceError(401, 'ERR_SESSION_CHANGED')

  let response: Response

  try {
    response = await fetch(path, {
      method,
      headers: {
        Authorization: `Bearer ${idToken}`,
        ...(body ? { 'Content-Type': 'application/json' } : {}),
      },
      ...(body ? { body: JSON.stringify(body) } : {}),
      signal: AbortSignal.timeout(15000),
      cache: 'no-store',
      credentials: 'omit',
      redirect: 'error',
    })
  } catch {
    throw new NotificationDeviceError(0, 'ERR_NETWORK')
  }

  if (!response.ok) {
    const payload: unknown = await response.json().catch(() => null)

    let code = 'ERR_REQUEST_FAILED'

    if (
      payload &&
      typeof payload === 'object' &&
      'error' in payload
    ) {
      const error = payload.error

      if (
        error &&
        typeof error === 'object' &&
        'code' in error &&
        typeof error.code === 'string'
      ) {
        code = error.code
      }
    }

    throw new NotificationDeviceError(response.status, code)
  }

  return response
}

export async function registerNotificationDevice(
  fcmToken: string,
  isCurrentSession: () => boolean = () => true,
): Promise<void> {
  if (
    !fcmToken ||
    /\s/.test(fcmToken) ||
    new TextEncoder().encode(fcmToken).length > 4096
  ) {
    throw new NotificationDeviceError(0, 'ERR_INVALID_FCM_TOKEN')
  }

  const deviceId = getOrCreateDeviceId()

  const response = await sendDeviceRequest(
    'POST',
    '/api/v1/notifications/devices',
    {
      deviceId,
      token: fcmToken,
    },
    isCurrentSession,
  )

  const payload: unknown = await response.json().catch(() => null)

  if (
    !payload ||
    typeof payload !== 'object' ||
    !('registered' in payload) ||
    payload.registered !== true ||
    !('deviceId' in payload) ||
    typeof payload.deviceId !== 'string' ||
    payload.deviceId.toLowerCase() !== deviceId.toLowerCase()
  ) {
    throw new NotificationDeviceError(
      response.status,
      'ERR_INVALID_RESPONSE',
    )
  }
}

export async function unregisterNotificationDevice(): Promise<void> {
  const deviceId = getSavedDeviceId()

  if (!deviceId) {
    return
  }

  const response = await sendDeviceRequest(
    'DELETE',
    `/api/v1/notifications/devices/${deviceId}`,
  )

  if (response.status !== 204) {
    throw new NotificationDeviceError(
      response.status,
      'ERR_INVALID_RESPONSE',
    )
  }
}
