import { getApp, getApps, initializeApp } from 'firebase/app'
import { browserSessionPersistence, getAuth, inMemoryPersistence, setPersistence, type Auth } from 'firebase/auth'
import { getMessaging, isSupported } from 'firebase/messaging'

const firebaseConfig = {
  apiKey: import.meta.env.VITE_FIREBASE_API_KEY,
  authDomain: import.meta.env.VITE_FIREBASE_AUTH_DOMAIN,
  projectId: import.meta.env.VITE_FIREBASE_PROJECT_ID,
  messagingSenderId: import.meta.env.VITE_FIREBASE_MESSAGING_SENDER_ID,
  appId: import.meta.env.VITE_FIREBASE_APP_ID,
}
const migratedAuthInstances = new WeakSet<Auth>()

export function isFirebaseConfigured() {
  return Object.values(firebaseConfig).every(
    (value) => typeof value === 'string' && value.trim().length > 0,
  )
}

export function getFirebaseApp() {
  if (!isFirebaseConfigured()) {
    return null
  }

  const hasDefaultApp = getApps().some(
    (app) => app.name === '[DEFAULT]',
  )

  return hasDefaultApp
    ? getApp()
    : initializeApp(firebaseConfig)
}

export function getFirebaseAuth() {
  const app = getFirebaseApp()
  if (!app) return null
  const auth = getAuth(app)
  if (!migratedAuthInstances.has(auth)) {
    migratedAuthInstances.add(auth)
    // Let the SDK restore and move any legacy AURA session out of persistent storage.
    void setPersistence(auth, browserSessionPersistence)
      .catch(() => setPersistence(auth, inMemoryPersistence))
      .catch(() => migratedAuthInstances.delete(auth))
  }
  return auth
}

export async function getFirebaseMessaging() {
  if (!isFirebaseConfigured()) {
    return null
  }

  if (!(await isSupported())) {
    return null
  }

  const app = getFirebaseApp()

  return app ? getMessaging(app) : null
}
