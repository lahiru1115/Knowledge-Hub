import { defineConfig, devices } from '@playwright/test'

/**
 * Browser tests against the real stack: PostgreSQL + Core API (http://localhost:8080) + this app.
 * Start the first two yourself (see e2e/README.md); Playwright starts the Vite dev server.
 * Locally, if the browser is not installed by Playwright, point PW_CHROMIUM_PATH at a Chromium binary.
 */
export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/support/global-setup.ts',
  fullyParallel: true,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'retain-on-failure',
    launchOptions: { executablePath: process.env.PW_CHROMIUM_PATH || undefined },
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'npm run dev -- --strictPort',
    url: 'http://localhost:5173',
    reuseExistingServer: !process.env.CI,
  },
})
