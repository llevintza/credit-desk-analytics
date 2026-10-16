import { expect, Page } from '@playwright/test';
import { saveSession } from './session';

/**
 * Logs in once through the real login page and saves the session (cookies, storage, and the tab's tokens) for the
 * other tests. The viewer (auth.setup.ts) and the admin (admin.setup.ts, #278) each save their own files.
 */
export async function logIn(page: Page, account: { email: string; password: string; stateFile: string; sessionFile: string }): Promise<void> {
  const { email, password } = account;

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
  await page.context().storageState({ path: account.stateFile });
  await saveSession(page, account.sessionFile);
}
