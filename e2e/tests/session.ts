import { BrowserContext, expect, test as base } from '@playwright/test';
import { readFileSync, writeFileSync } from 'node:fs';

/**
 * ADR-0024: the SPA keeps its refresh token in sessionStorage, which Playwright's storageState doesn't save. The
 * setup project writes this tab's sessionStorage here, next to state.json (both under the gitignored .auth/).
 */
export const sessionFile = '.auth/session.json';

/** Saves the signed-in page's sessionStorage for the other tests. */
export async function saveSession(page: import('@playwright/test').Page): Promise<void> {
  const entries = await page.evaluate(() => Object.entries(sessionStorage));
  writeFileSync(sessionFile, JSON.stringify(entries));
}

/**
 * Puts the saved session into every page of the context before the app starts. Only where the tab has none yet,
 * so the token a refresh renewed in this tab isn't swapped back on the next navigation. The saved refresh token stays
 * valid for the run: tokens are stateless, and no test signs it out on the server.
 */
export async function withSession(context: BrowserContext): Promise<void> {
  const entries = JSON.parse(readFileSync(sessionFile, 'utf8')) as [string, string][];
  await context.addInitScript((saved) => {
    for (const [key, value] of saved) if (sessionStorage.getItem(key) === null) sessionStorage.setItem(key, value);
  }, entries);
}

/** Playwright's test with the signed-in session restored in every test's context. */
export const test = base.extend({
  context: async ({ context }, use) => {
    await withSession(context);
    await use(context);
  },
});

export { expect };
