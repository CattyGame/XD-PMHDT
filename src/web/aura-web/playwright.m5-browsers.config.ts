import { defineConfig, devices } from '@playwright/test'
import base from './playwright.m5-mock.config'

export default defineConfig({
  ...base,
  outputDir: 'test-results/m5-browsers',
  projects: [
    { name: 'chrome', use: { ...devices['Desktop Chrome'], channel: 'chrome' } },
    { name: 'msedge', use: { ...devices['Desktop Edge'], channel: 'msedge' } },
    { name: 'firefox', use: { ...devices['Desktop Firefox'] } },
  ],
})
