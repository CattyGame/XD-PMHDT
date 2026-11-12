import { defineConfig } from '@playwright/test'
import base from './playwright.config'

const existingServer = process.env.AURA_BATCH_MOCK_BASE_URL
const origin = existingServer || 'http://127.0.0.1:5176'
if (new URL(origin).hostname !== '127.0.0.1') throw new Error('Batch mocks require a loopback frontend.')

export default defineConfig({
  ...base,
  testMatch: '**/batch-frontend.mock.e2e.ts',
  reporter: 'list',
  outputDir: 'test-results/batch-mock',
  use: { ...base.use, baseURL: origin, serviceWorkers: 'block' },
  webServer: existingServer ? undefined : {
    command: 'node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 5176 --strictPort',
    url: origin,
    reuseExistingServer: false,
    env: {
      VITE_ENABLE_MOCK: 'false',
      VITE_FIREBASE_API_KEY: 'playwright-fixture-key',
      VITE_FIREBASE_AUTH_DOMAIN: 'aura-fixture.invalid',
      VITE_FIREBASE_PROJECT_ID: 'aura-fixture',
      VITE_FIREBASE_MESSAGING_SENDER_ID: '100000000000',
      VITE_FIREBASE_APP_ID: '1:100000000000:web:aura-fixture',
    },
  },
})
