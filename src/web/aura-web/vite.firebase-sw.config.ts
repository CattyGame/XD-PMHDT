import { fileURLToPath } from 'node:url'
import { defineConfig, loadEnv } from 'vite'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), 'VITE_FIREBASE_')

  const required = [
    'VITE_FIREBASE_API_KEY',
    'VITE_FIREBASE_AUTH_DOMAIN',
    'VITE_FIREBASE_PROJECT_ID',
    'VITE_FIREBASE_MESSAGING_SENDER_ID',
    'VITE_FIREBASE_APP_ID',
  ]

  const configured = required.some(name => env[name]?.trim())
  for (const name of required) {
    if (configured && !env[name]?.trim()) {
      throw new Error(`Thiếu cấu hình Firebase cho worker: ${name}`)
    }
  }

  return {
    publicDir: false,
    build: {
      outDir: 'public',
      emptyOutDir: false,
      sourcemap: false,
      lib: {
        entry: fileURLToPath(
          new URL(
            './src/firebase/firebaseMessagingWorker.js',
            import.meta.url,
          ),
        ),
        name: 'AuraFirebaseMessagingWorker',
        formats: ['iife'],
        fileName: () => 'firebase-messaging-sw.js',
      },
    },
  }
})
