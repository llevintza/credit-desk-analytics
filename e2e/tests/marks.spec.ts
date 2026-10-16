import { expect, test } from './session';
import { gridReady, watchConsole } from './helpers';

test.describe('P6 intraday overlay (README §6 P6, #71/#72)', () => {
  test('live marks stream with the bearer token, flash changed cells and refresh the totals from the server', async ({ page }) => {
    const errors = watchConsole(page);
    await page.goto('/positions');
    await gridReady(page);

    const stream = page.waitForRequest((r) => r.url().includes('/api/marks/stream'));
    const totals = page.waitForResponse((r) => r.url().endsWith('/api/marks/totals') && r.request().method() === 'POST');
    const reload = page.waitForRequest((r) => r.url().endsWith('/api/positions/query') && (r.postDataJSON() as { columns: string[] }).columns.includes('bond_id'));
    await page.getByTestId('live-marks').click();
    await expect(page.getByTestId('live-marks')).toHaveAttribute('aria-pressed', 'true');

    // fetch, not EventSource: the stream carries the session's bearer token like any other call (ADR-0015, ADR-0024).
    expect((await stream).headers()['authorization']).toMatch(/^Bearer \S+/);
    await reload; // the blocks now carry bond_id, the key marks arrive by
    await expect(page.getByTestId('live-status')).toContainText(/Live · \d+ bonds/);

    // Each new mark flashes its price and spread cells in the body rows (AG Grid's change highlight); every 4th
    // bond of the default order is marked, so the first screen has some. The pinned Total row is not counted here.
    await expect(page.locator('.ag-body-viewport .ag-cell-data-changed').first()).toBeAttached({ timeout: 30_000 });

    // The pinned totals come from the server with the marks applied.
    const res = await totals;
    expect(res.status()).toBe(200);
    const body = (await res.json()) as { seq: number; summary: Record<string, number | null> };
    expect(body.seq).toBeGreaterThan(0);
    expect(Object.keys(body.summary).sort()).toEqual(['price', 'spread_bp']);

    // Off again: the stream closes and the blocks stop asking for bond_id.
    await page.getByTestId('live-marks').click();
    await expect(page.getByTestId('live-status')).toHaveCount(0);
    expect(errors).toEqual([]);
  });
});
