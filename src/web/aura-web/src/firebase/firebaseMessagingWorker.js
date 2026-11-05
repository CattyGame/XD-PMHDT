import { initializeApp } from 'firebase/app'
import {
  getMessaging,
  onBackgroundMessage,
} from 'firebase/messaging/sw'

self.addEventListener('install', (event) => {
  event.waitUntil(self.skipWaiting())
})

self.addEventListener('activate', (event) => {
  event.waitUntil(self.clients.claim())
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  event.stopImmediatePropagation()

  event.waitUntil(
    (async () => {
      const windows = await self.clients.matchAll({
        type: 'window',
        includeUncontrolled: true,
      })

      const existing = windows.find(
        (client) => new URL(client.url).origin === self.location.origin,
      )
      const data = event.notification.data?.FCM_MSG?.data ?? event.notification.data ?? {}
      const candidate = data.targetPath ?? '/notifications'
      const allowed = /^\/(?:analyses|batches|billing\/orders)\/[0-9a-f-]{36}$/i.test(candidate)
      const target = new URL(allowed ? candidate : '/notifications', self.location.origin).href

      if (existing) {
        await existing.navigate(target)
        return existing.focus()
      }

      return self.clients.openWindow(target)
    })(),
  )
})

const app = initializeApp({
  apiKey: import.meta.env.VITE_FIREBASE_API_KEY,
  authDomain: import.meta.env.VITE_FIREBASE_AUTH_DOMAIN,
  projectId: import.meta.env.VITE_FIREBASE_PROJECT_ID,
  messagingSenderId: import.meta.env.VITE_FIREBASE_MESSAGING_SENDER_ID,
  appId: import.meta.env.VITE_FIREBASE_APP_ID,
})

const messaging = getMessaging(app)

onBackgroundMessage(messaging, (payload) => {
  if (payload.notification) {
    return
  }

  return self.registration.showNotification('AURA - Thông báo mới', {
    body: 'Bạn có thông báo mới. Mở AURA để xem chi tiết.',
    tag: 'aura-notification',
  })
})
