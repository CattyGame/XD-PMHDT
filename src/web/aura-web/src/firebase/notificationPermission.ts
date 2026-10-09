
export type NotificationResult =
  | 'granted'
  | 'denied'
  | 'default'
  | 'unsupported'

export function getNotificationPermission(): NotificationResult {
  if (!('Notification' in window)) {
    return 'unsupported'
  }

  return Notification.permission
}

export async function requestNotificationPermission():
  Promise<NotificationResult> {

  if (!('Notification' in window)) {
    return 'unsupported'
  }

  // Chỉ gọi hàm này sau khi người dùng bấm nút.
  if (Notification.permission === 'denied') {
    return 'denied'
  }

  if (Notification.permission === 'granted') {
    return 'granted'
  }

  return await Notification.requestPermission()
}
