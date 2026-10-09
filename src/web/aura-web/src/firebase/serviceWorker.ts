
export async function registerFirebaseServiceWorker():
  Promise<ServiceWorkerRegistration | null> {

  if (!('serviceWorker' in navigator)) {
    console.warn('Trình duyệt không hỗ trợ Service Worker')
    return null
  }

  if (!window.isSecureContext) {
    console.warn('FCM cần HTTPS hoặc localhost')
    return null
  }

  try {
    const registration = await navigator.serviceWorker.register(
      '/firebase-messaging-sw.js',
      { scope: '/firebase-messaging-scope/' }
    )

    console.log('Firebase Service Worker đã đăng ký')
    return registration

  } catch (error) {
    console.error('Không thể đăng ký Service Worker:', error)
    return null
  }
}
