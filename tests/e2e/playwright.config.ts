import { defineConfig } from '@playwright/test'

const base = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:6273'

export default defineConfig({
  testDir: './specs',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  workers: 1,                       // one shared demo database; specs sign in as distinct users but change the same trains
  fullyParallel: false,
  retries: 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: base,
    trace: 'retain-on-failure',
    launchOptions: process.env.E2E_CHROMIUM ? { executablePath: process.env.E2E_CHROMIUM } : {},
  },
  webServer: process.env.E2E_BASE_URL ? undefined : {
    command: 'node run-app.mjs',
    url: `${base}/healthz`,
    timeout: 240_000,
    reuseExistingServer: !process.env.CI,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 60_000 },
  },
})
