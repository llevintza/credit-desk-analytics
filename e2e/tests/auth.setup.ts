import { expect, test as setup } from '@playwright/test';
import { saveSession } from './session';

/** Logs in once through the real login page and saves the session (cookies, storage, and the tab's tokens) for the other tests. */
setup('log in', async ({ page }) => {
  const email = process.env.DESK_EMAIL;
  const password = process.env.DESK_PASSWORD;
  if (!email || !password) throw new Error('Set DESK_EMAIL and DESK_PASSWORD (create the account with Desk.UserAdmin).');

  await page.goto('/positions');
  await expect(page).toHaveURL(/\/login$/); // no session: the data is not public
  await page.getByTestId('email').fill(email);
  // Not fill(): its step title carries the typed value into the public HTML report (#155). evaluate's title is just
  // "Evaluate", and the input event is what a typed value fires. reporters/secret-guard.ts fails the run on a leak.
  await page.getByTestId('password').evaluate((input: HTMLInputElement, value) => {
    input.value = value;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }, password);
  await page.getByTestId('sign-in').click();
  await expect(page).toHaveURL(/\/positions$/);
  await expect(page.getByTestId('positions-grid')).toBeVisible();
  // Bearer tokens (ADR-0024): no session cookie is set, and the refresh token is in this tab's sessionStorage.
  expect((await page.context().cookies()).map((c) => c.name)).toEqual([]);
  expect(await page.evaluate(() => sessionStorage.getItem('desk.refresh'))).toBeTruthy();
  await page.context().storageState({ path: '.auth/state.json' });
  await saveSession(page);
});
