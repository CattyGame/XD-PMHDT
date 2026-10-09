
export function checkNotificationPermission() {
  if (!('Notification' in window)) {
    return 'UNSUPPORTED'
  }

  if (Notification.permission === 'denied') {
    return 'PERMISSION_DENIED'
  }

  if (Notification.permission === 'granted') {
    return 'PERMISSION_GRANTED'
  }

  return 'PERMISSION_DEFAULT'
}
