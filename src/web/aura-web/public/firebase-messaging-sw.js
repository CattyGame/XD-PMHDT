
/*
 * SCRUM-296 - AURA Firebase Messaging Service Worker
 * Chỉ xử lý thông báo an toàn, không chứa dữ liệu bệnh nhân.
 */

self.addEventListener('push', function (event) {
  // Chỉ dùng cho bản demo MSW/local.
  // FCM background thật sẽ được tích hợp sau khi có cấu hình.
  let data = {}

  try {
    data = event.data ? event.data.json() : {}
  } catch {
    data = {}
  }

  // Không lấy thông tin y tế từ payload để hiển thị.
  const title = 'AURA - Thông báo mới'

  const options = {
    body: 'Bạn có thông báo mới. Mở AURA để xem chi tiết.',
    icon: '/vite.svg',
    badge: '/vite.svg',
    tag: 'aura-notification',
    data: {
      url: '/notifications',
    },
  }

  event.waitUntil(
    self.registration.showNotification(title, options)
  )
})

self.addEventListener('notificationclick', function (event) {
  event.notification.close()

  event.waitUntil(
    clients.openWindow('/notifications')
  )
})
