import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  timeout: 45000,
  expect: { timeout: 12000 },
  workers: 1,
  use: {
    baseURL: process.env.UI_BASE_URL || 'http://localhost:3000',
    ...devices['Desktop Chrome'],
    channel: 'chrome',
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
});
