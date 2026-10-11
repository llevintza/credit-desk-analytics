import { Page, expect, test } from '@playwright/test';
import { gridReady } from './helpers';

/** README §11: every page in dark and light, saved to docs/screenshots/ and attached to the PR. */
const pages = [
  { path: '/positions', name: 'positions' },
  { path: '/funds', name: 'fund-performance' },
  { path: '/insights', name: 'insights' },
  { path: '/deals', name: 'deals' },
  { path: '/lab', name: 'performance-lab' },
];
const dir = '../docs/screenshots';

async function setTheme(page: Page, theme: 'dark' | 'light') {
  await page.evaluate((t) => localStorage.setItem('desk.settings', JSON.stringify({ theme: t, palette: 'standard', negatives: 'minus' })), theme);
}

for (const theme of ['dark', 'light'] as const) {
  test(`screenshots: every page, ${theme}`, async ({ page }) => {
    await page.goto('/positions');
    await setTheme(page, theme);
    for (const p of pages) {
      await page.goto(p.path);
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      if (p.name === 'positions') await gridReady(page);
      if (p.name === 'fund-performance') await expect(page.getByTestId('caption')).toBeVisible();
      await page.waitForTimeout(400);
      await page.screenshot({ path: `${dir}/${p.name}-${theme}.png` });
    }
  });

  test(`screenshots: login, ${theme}`, async ({ browser }) => {
    const page = await browser.newPage({ viewport: { width: 1600, height: 900 } }); // no session
    await page.goto('/login');
    await setTheme(page, theme);
    await page.reload();
    await expect(page.getByTestId('sign-in')).toBeVisible();
    await page.screenshot({ path: `${dir}/login-${theme}.png` });
    await page.close();
  });

  test(`screenshots: unconfirmed sign-out, ${theme}`, async ({ browser }) => {
    // A fresh context from a copy of the saved session: the 403 route and the local sign-out stay in it, and the
    // fulfilled logout never reaches the server, so the shared session the other tests use stays signed in.
    const context = await browser.newContext({ storageState: '.auth/state.json', viewport: { width: 1600, height: 900 } });
    const page = await context.newPage();
    await page.route('**/api/auth/logout', (route) =>
      route.request().method() === 'POST' ? route.fulfill({ status: 403, body: '' }) : route.continue(),
    );
    await page.goto('/positions');
    await setTheme(page, theme);
    await page.reload();
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    await page.getByTestId('user-menu').locator('summary').click();
    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/login\?signout=unconfirmed$/);
    await expect(page.getByTestId('signout-unconfirmed')).toBeVisible();
    await page.screenshot({ path: `${dir}/signout-unconfirmed-${theme}.png` });
    await context.close();
  });
}
