import { defineConfig, devices } from '@playwright/test'

// SCRUM-357: Cháº¡y vá»›i frontend sáºµn cÃ³. KhÃ´ng khá»Ÿi Ä‘á»™ng Docker hoáº·c dÃ¹ng dá»¯ liá»‡u y táº¿ tháº­t.
export default defineConfig({
  testDir: './e2e',
  testMatch: '**/scrum357.e2e.ts',
  fullyParallel: false,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]] : 'list',
  use: {
    baseURL: process.env.SCRUM357_BASE_URL || 'http://127.0.0.1:5175',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    actionTimeout: 10_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  outputDir: 'test-results/scrum357',
  timeout: 45_000,
})
