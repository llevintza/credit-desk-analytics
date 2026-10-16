import { readFileSync } from 'node:fs';
import { expect, flushSession, sessionFile, test, withSession } from './session';

/** The refresh token the next context will start from. */
function savedRefreshToken(): string {
  const saved = JSON.parse(readFileSync(sessionFile, 'utf8')) as [string, string][];
  return saved.find(([key]) => key === 'desk.refresh')![1];
}

test.describe('the shared session (e2e/tests/session.ts)', () => {
  test('a refresh the page navigates away from still saves the renewed token (#480)', async ({ browser }) => {
    const context = await browser.newContext({ storageState: '.auth/state.json' });
    await withSession(context);
    const page = await context.newPage();
    const before = savedRefreshToken();
    // The page's own route runs before the session's: it holds the app's first refresh until the tab has navigated
    // away, then lets it go on to the server. That is the order a reload right after a navigation can take, made
    // certain, and the one refresh in this context.
    let held = false;
    await page.route('**/api/auth/refresh', async (route) => {
      held = true;
      await page.goto('about:blank');
      await route.fallback();
    });
    await page.goto('/positions', { waitUntil: 'commit' });
    await expect.poll(() => held).toBe(true);
    await expect(page).toHaveURL('about:blank');
    await flushSession();
    // The server swapped the token, so the file must hold the renewed one: the old one is spent (#353). Compared as
    // a boolean, so a failure doesn't print a token into the public report.
    expect(savedRefreshToken() !== before, 'the session file holds the renewed refresh token').toBe(true);
    await page.unrouteAll({ behavior: 'wait' });
    await context.close();

    // The saved token starts the next context signed in.
    const next = await browser.newContext({ storageState: '.auth/state.json' });
    await withSession(next);
    const fresh = await next.newPage();
    await fresh.goto('/positions');
    await expect(fresh.getByTestId('positions-grid')).toBeVisible();
    await expect(fresh).toHaveURL(/\/positions$/);
    await flushSession();
    await next.close();
  });
});
