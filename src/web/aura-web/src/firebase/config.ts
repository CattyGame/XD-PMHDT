
import { getApp, getApps, initializeApp } from 'firebase/app'
import { getMessaging, isSupported } from 'firebase/messaging'

const firebaseConfig = {
  apiKey: import.meta.env.VITE_FIREBASE_API_KEY,
  authDomain: import.meta.env.VITE_FIREBASE_AUTH_DOMAIN,
  projectId: import.meta.env.VITE_FIREBASE_PROJECT_ID,
  messagingSenderId: import.meta.env.VITE_FIREBASE_MESSAGING_SENDER_ID,
  appId: import.meta.env.VITE_FIREBASE_APP_ID,
}

export function isFirebaseConfigured() {
  return Object.values(firebaseConfig).every(
    (value) => typeof value === 'string' && value.length > 0
  )
}

export async function getFirebaseMessaging() {
  // Chưa được cấp cấu hình thì không khởi tạo Firebase.
  if (!isFirebaseConfigured()) {
    return null
  }

  // Kiểm tra trình duyệt có hỗ trợ Firebase Messaging không.
  if (!(await isSupported())) {
    return null
  }

  // Tránh khởi tạo Firebase App nhiều lần.
  const app = getApps().length > 0
    ? getApp()
    : initializeApp(firebaseConfig)

  return getMessaging(app)
}
