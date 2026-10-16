import { BrowserContext, Route, expect, test as base } from '@playwright/test';
import { readFileSync, writeFileSync } from 'node:fs';

/**
 * ADR-0024: the SPA keeps its refresh token in sessionStorage, which Playwright's storageState doesn't save. The
 * setup project writes this tab's sessionStorage here, next to state.json (both under the gitignored .auth/).
 */
export const sessionFile = '.auth/session.json';

/** The admin account's session (#278): the admin project's own file, so the two accounts' tokens never mix. */
export const adminSessionFile = '.auth/admin-session.json';

/** Saves the signed-in page's sessionStorage for the other tests. */
export async function saveSession(page: import('@playwright/test').Page, file = sessionFile): Promise<void> {
  const entries = await page.evaluate(() => Object.entries(sessionStorage));
  writeFileSync(file, JSON.stringify(entries));
}

/** The SPA's sessionStorage key for the refresh token (web/src/app/core/token.store.ts). */
const refreshKey = 'desk.refresh';

/** Refreshes still on their way through the route below. */
const pending = new Set<Promise<void>>();

/** Puts a renewed refresh token into the session file, keeping the other saved entries. */
function saveRefreshToken(file: string, token: string): void {
  const saved = JSON.parse(readFileSync(file, 'utf8')) as [string, string][];
  writeFileSync(file, JSON.stringify(saved.map(([key, value]) => [key, key === refreshKey ? token : value])));
}

/**
 * Puts the saved session into every page of the context before the app starts. Only where the tab has none yet,
 * so the token a refresh renewed in this tab isn't swapped back on the next navigation.
 * Refresh tokens are single-use (#353): one that comes back after it was swapped ends the session. So every refresh
 * in the context writes its new token back to the session file, and the next context starts from that one. The run
 * has one worker and one page per context, so there is only ever one live token.
 * The refresh goes through a route that sends it itself and saves the token before the page gets the answer (#480).
 * Reading it from the page's response instead lost it whenever a reload or navigation came first: the browser drops
 * the body ("navigated away from"), the server had already swapped the token, and the file kept the dead one.
 */
export async function withSession(context: BrowserContext, file = sessionFile): Promise<void> {
  const entries = JSON.parse(readFileSync(file, 'utf8')) as [string, string][];
  await context.addInitScript((saved) => {
    for (const [key, value] of saved) if (sessionStorage.getItem(key) === null) sessionStorage.setItem(key, value);
  }, entries);
  await context.route('**/api/auth/refresh', (route) => {
    if (route.request().method() !== 'POST') return route.fallback();
    const write: Promise<void> = forwardRefresh(route, file).finally(() => pending.delete(write));
    pending.add(write);
    return write;
  });
}

/** Sends the page's refresh, saves the renewed token, then hands the page the server's answer unchanged. */
async function forwardRefresh(route: Route, file: string): Promise<void> {
  const response = await route.fetch();
  if (response.status() === 200) saveRefreshToken(file, ((await response.json()) as { refreshToken: string }).refreshToken);
  // The page may have navigated away by now. The token is saved either way, and a reloaded tab refreshes again with
  // the one it still holds, which the grace window answers with the current one (ADR-0024).
  await route.fulfill({ response }).catch(() => undefined);
}

/** Waits until every refreshed token seen so far is in the session file. Call it before closing a context. */
export async function flushSession(): Promise<void> {
  await Promise.all([...pending]);
}

/** Playwright's test with the signed-in session restored in every test's context. */
export const test = base.extend({
  context: async ({ context }, use) => {
    await withSession(context);
    await use(context);
    await flushSession();
  },
});

/** The same, signed in as the admin account (the admin project, #278). */
export const adminTest = base.extend({
  context: async ({ context }, use) => {
    await withSession(context, adminSessionFile);
    await use(context);
    await flushSession();
  },
});

export { expect };
