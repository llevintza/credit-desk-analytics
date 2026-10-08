import { Page, Request, expect } from '@playwright/test';

export const queryUrl = '/api/positions/query';

/** Waits until the grid has rows (any rendered row index) and the status bar has a total. */
export async function gridReady(page: Page): Promise<void> {
  await expect(page.locator('[row-index="0"]').first()).toBeVisible();
  await expect(page.getByTestId('rows')).toContainText('total');
}

/** Records positions query requests from now on: the ones that completed and the ones that were aborted. */
export function watchQueries(page: Page) {
  const finished: Request[] = [];
  const failed: Request[] = [];
  page.on('requestfinished', (r) => { if (r.url().endsWith(queryUrl)) finished.push(r); });
  page.on('requestfailed', (r) => { if (r.url().endsWith(queryUrl)) failed.push(r); });
  return { finished, failed, bodies: () => finished.map((r) => r.postDataJSON() as { columns: string[]; quickFilter?: string; startRow: number }) };
}

/** Console errors, ignoring the expected 401 from the pre-login session check. */
export function watchConsole(page: Page): string[] {
  const errors: string[] = [];
  // The served app runs under the API's CSP (ADR-0005): any violation is an error.
  void page.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (e) => console.error(`CSP violation: ${e.violatedDirective} ${e.blockedURI}`));
  });
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    const text = m.text();
    if (/401 \(Unauthorized\)/.test(text)) return;
    errors.push(text);
  });
  page.on('pageerror', (e) => errors.push(e.message));
  return errors;
}
