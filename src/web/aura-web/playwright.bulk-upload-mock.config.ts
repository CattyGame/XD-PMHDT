import { defineConfig } from '@playwright/test'
import base from './playwright.config'

export default defineConfig({
  ...base,
  testMatch: '**/bulk-upload.mock.e2e.ts',
  reporter: 'list',
  outputDir: 'test-results/bulk-upload-mock',
  use: { ...base.use, baseURL: process.env.AURA_BULK_MOCK_BASE_URL || 'http://127.0.0.1:5175' },
})
