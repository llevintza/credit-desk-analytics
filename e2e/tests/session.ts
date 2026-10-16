import { BrowserContext, expect, test as base } from '@playwright/test';
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

/** Rotated refresh tokens still being written back to the session file. */
const pending = new Set<Promise<void>>();

/**
 * Puts the saved session into every page of the context before the app starts. Only where the tab has none yet,
 * so the token a refresh renewed in this tab isn't swapped back on the next navigation.
 * Refresh tokens are single-use (#353): one that comes back after it was swapped ends the session. So every refresh
 * in the context writes its new token back to the session file, and the next context starts from that one. The run
 * has one worker and one page per context, so there is only ever one live token.
 */
export async function withSession(context: BrowserContext, file = sessionFile): Promise<void> {
  const entries = JSON.parse(readFileSync(file, 'utf8')) as [string, string][];
  await context.addInitScript((saved) => {
    for (const [key, value] of saved) if (sessionStorage.getItem(key) === null) sessionStorage.setItem(key, value);
  }, entries);
  context.on('response', (res) => {
    if (res.request().method() !== 'POST' || new URL(res.url()).pathname !== '/api/auth/refresh' || res.status() !== 200) return;
    const write: Promise<void> = res
      .json()
      .then((tokens: { refreshToken: string }) => {
        const saved = JSON.parse(readFileSync(file, 'utf8')) as [string, string][];
        writeFileSync(file, JSON.stringify(saved.map(([key, value]) => [key, key === refreshKey ? tokens.refreshToken : value])));
      })
      .finally(() => pending.delete(write));
    pending.add(write);
  });
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
