import { defineConfig, devices } from '@playwright/test';

/**
 * README §11 e2e: runs against the docker compose stack (BASE_URL, default http://localhost:8080), seeded,
 * with an account from Desk.UserAdmin in DESK_EMAIL / DESK_PASSWORD (never committed).
 */
export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  use: {
    baseURL: process.env.BASE_URL ?? 'http://localhost:8080',
    viewport: { width: 1600, height: 900 },
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'setup', testMatch: /auth\.setup\.ts/ },
    { name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1600, height: 900 }, storageState: '.auth/state.json' }, dependencies: ['setup'] },
  ],
});
