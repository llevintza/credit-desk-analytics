import { defineConfig, devices } from '@playwright/test';

/**
 * README §11 e2e: runs against the docker compose stack (BASE_URL, default http://localhost:8080), seeded,
 * with an account from Desk.UserAdmin in DESK_EMAIL / DESK_PASSWORD (never committed).
 */
export default defineConfig({
  testDir: './tests',
  // The ADR-0009 (PERF) and ADR-0014 (DEMO_SESSION) measurement runs are opt-in: without their variable they are not
  // collected at all, so the default run reports 0 skipped (#210). Each spec keeps its own test.skip guard as a backstop.
  testIgnore: [...(process.env.PERF ? [] : ['**/perf.spec.ts']), ...(process.env.DEMO_SESSION ? [] : ['**/demo-session.spec.ts'])],
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  // secret-guard runs after html (reporters end in order) and fails the run if the password is in the report (#155).
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }], ['./reporters/secret-guard.ts']],
  use: {
    baseURL: process.env.BASE_URL ?? 'http://localhost:8080',
    viewport: { width: 1600, height: 900 },
    trace: 'retain-on-failure',
  },
  projects: [
    // The login enters the password without a recorded value, and records no trace either (#155).
    { name: 'setup', testMatch: /auth\.setup\.ts/, use: { trace: 'off' } },
    // The guard's own checks, next to it: no browser, no stack.
    { name: 'guard', testDir: './reporters' },
    { name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1600, height: 900 }, storageState: '.auth/state.json' }, dependencies: ['setup'] },
  ],
});
