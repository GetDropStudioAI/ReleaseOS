import { defineConfig, devices } from '@playwright/test'

const base = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:6273'

// REOS-149: chromium is the per-PR gate and is always defined. Edge, Firefox and WebKit are defined only when E2E_CROSS_BROWSER=1
// (the cross-browser workflow sets it), so a plain `npx playwright test` still runs exactly the chromium suite, as the gate always has.
const crossBrowser = process.env.E2E_CROSS_BROWSER === '1'

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
  },
  projects: [
    {
      name: 'chromium',   // no device preset: the gate's browser settings stay exactly what they were
      use: { launchOptions: process.env.E2E_CHROMIUM ? { executablePath: process.env.E2E_CHROMIUM } : {} },
    },
    ...(crossBrowser ? [
      { name: 'msedge', use: { ...devices['Desktop Edge'], channel: 'msedge' } },   // needs Microsoft Edge installed (it is on windows-latest)
      { name: 'firefox', use: { ...devices['Desktop Firefox'] } },
      { name: 'webkit', use: { ...devices['Desktop Safari'] } },
    ] : []),
  ],
  webServer: process.env.E2E_BASE_URL ? undefined : {
    command: 'node run-app.mjs',
    url: `${base}/healthz`,
    timeout: 240_000,
    reuseExistingServer: !process.env.CI,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 60_000 },
  },
})
