import { initializeApp } from 'firebase/app'
import { getMessaging, onBackgroundMessage } from 'firebase/messaging/sw'

import { safeAnalysisPath } from './notificationNavigation'

self.addEventListener('install', event => { event.waitUntil(self.skipWaiting()) })
self.addEventListener('activate', event => { event.waitUntil(self.clients.claim()) })

// Đăng ký click handler TRƯỚC Firebase SDK để chặn điều hướng mặc định không an toàn.
self.addEventListener('notificationclick', event => {
  event.notification.close()
  event.stopImmediatePropagation()
  const raw = event.notification.data
  const fcm = raw && typeof raw === 'object' ? raw.FCM_MSG : null
  const data = fcm?.data ?? raw
  const path = safeAnalysisPath(data?.targetPath, data?.analysisId) ?? '/notifications'
  event.waitUntil((async () => {
    const url = new URL(path, self.location.origin).href
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
    const existing = windows.find(client => {
      try { return new URL(client.url).origin === self.location.origin } catch { return false }
    })
    if (existing) {
      await existing.navigate(url)
      return existing.focus()
    }
    return self.clients.openWindow(url)
  })())
})

const app = initializeApp({
  apiKey: import.meta.env.VITE_FIREBASE_API_KEY,
  authDomain: import.meta.env.VITE_FIREBASE_AUTH_DOMAIN,
  projectId: import.meta.env.VITE_FIREBASE_PROJECT_ID,
  messagingSenderId: import.meta.env.VITE_FIREBASE_MESSAGING_SENDER_ID,
  appId: import.meta.env.VITE_FIREBASE_APP_ID,
})
const messaging = getMessaging(app)

onBackgroundMessage(messaging, payload => {
  // Notification payload được Firebase SDK xử lý mặc định một lần; không tạo popup trùng.
  // M3 phải bảo đảm title/body không chứa dữ liệu y khoa nhạy cảm.
  if (payload.notification) return
  const path = safeAnalysisPath(payload.data?.targetPath, payload.data?.analysisId)
  return self.registration.showNotification('AURA - Có thông báo mới', {
    body: 'Mở AURA để xem chi tiết.',
    tag: payload.messageId ?? 'aura-notification',
    data: { targetPath: path ?? '/notifications' },
  })
})
