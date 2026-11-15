import { defineConfig } from '@playwright/test'
import base from './playwright.batch-mock.config'

export default defineConfig({
  ...base,
  testMatch: ['**/batch-frontend.mock.e2e.ts', '**/bulk-upload.mock.e2e.ts', '**/m5-workflows.mock.e2e.ts'],
  workers: 1,
  outputDir: 'test-results/m5-mock',
})
