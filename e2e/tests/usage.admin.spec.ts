import { Page } from '@playwright/test';
import { adminTest as test, expect } from './session';
import { watchConsole } from './helpers';

/**
 * #278 (from #70): the admin Usage page against the real stack, signed in as the run's throwaway admin. The e2e
 * compose override flushes the audit log every second, so the admin's own requests show up within a few refreshes.
 */
const admin = process.env.DESK_ADMIN_EMAIL ?? '';
const dir = '../docs/screenshots';

async function openUsage(page: Page) {
  const report = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/admin/usage');
  await page.goto('/usage');
  return report;
}

test.describe('Usage (README §15 phase 9, #70)', () => {
  test('reads the audit log live: the admin\'s own requests appear, per day and per user', async ({ page }) => {
    const errors = watchConsole(page);
    const res = await openUsage(page);
    expect(res.status()).toBe(200);
    expect(new URL(res.url()).searchParams.get('days')).toBe('7');
    expect(res.headers()['cache-control']).toContain('no-store');
    await expect(page.getByRole('link', { name: /Usage/ })).toBeVisible();
    await expect(page.getByTestId('caption')).toContainText('(UTC days)');

    // Today only: the window is one UTC day, from and to the same date.
    await page.getByTestId('window-1').click();
    await expect(page.getByTestId('window-1')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByTestId('caption')).toHaveText(/^(\d{4}-\d{2}-\d{2}) to \1 /);

    // The page's own loads are audited; a refresh after the flush counts them.
    await expect(async () => {
      await page.getByTestId('refresh').click();
      await expect(page.getByTestId('users')).toContainText(admin, { timeout: 2_000 });
    }).toPass({ timeout: 30_000 });
    expect(Number((await page.getByTestId('kpi-requests').innerText()).replace(/\D/g, ''))).toBeGreaterThan(0);
    await expect(page.getByTestId('days').locator('tbody tr')).toHaveCount(1);
    await expect(page.getByTestId('slowest')).toContainText('GET /api/');
    expect(errors).toEqual([]);
  });

  test('a new window supersedes the one in flight', async ({ page }) => {
    // Hold back the 30-day report, so it would land after the 1-day one if anything let it.
    await page.route(/\/api\/admin\/usage\?days=30\b/, async (route) => {
      await new Promise((resolve) => setTimeout(resolve, 1500));
      await route.continue().catch(() => undefined); // the SPA may have cancelled it meanwhile
    });
    await openUsage(page);
    await expect(page.getByTestId('usage')).toBeVisible();
    await page.getByTestId('window-30').click();
    await page.getByTestId('window-1').click();
    await expect(page.getByTestId('caption')).toHaveText(/^(\d{4}-\d{2}-\d{2}) to \1 /);
    await page.waitForTimeout(2000);
    await expect(page.getByTestId('caption')).toHaveText(/^(\d{4}-\d{2}-\d{2}) to \1 /);
    await expect(page.getByTestId('window-1')).toHaveAttribute('aria-pressed', 'true');
  });

  test('Clear server caches calls the admin endpoint and says so', async ({ page }) => {
    await openUsage(page);
    const cleared = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/admin/cache/clear' && r.request().method() === 'POST');
    await page.getByTestId('clear-cache').click();
    expect((await cleared).ok()).toBe(true);
    await expect(page.getByTestId('clear-status')).toHaveText('Server caches cleared.');
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`screenshot: Usage, ${theme}`, async ({ page }) => {
      // Set before the app starts, so the page loads once (a second navigation can cut off a token refresh).
      await page.addInitScript((t) => localStorage.setItem('desk.settings', JSON.stringify({ theme: t, palette: 'standard', negatives: 'minus' })), theme);
      await openUsage(page);
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      await expect(page.getByTestId('usage')).toBeVisible();
      await page.waitForTimeout(400);
      await page.screenshot({ path: `${dir}/usage-${theme}.png` });
    });
  }
});
