import { Page, Request, expect, test } from '@playwright/test';
import { watchConsole } from './helpers';

const sources = ['core', 'market', 'surveillance', 'pricing', 'reference'] as const;
const counts: Record<(typeof sources)[number], number> = { core: 6, market: 3, surveillance: 5, pricing: 3, reference: 3 };

const tiles = (page: Page, source: string, state?: string) =>
  page.locator(`[data-source=${source}]${state ? `[data-state=${state}]` : ''}`);

/** Paint marks the board leaves (README §10 P3): ms from the inputs settling to each source's tiles painted. */
async function paintTimes(page: Page): Promise<Record<string, number>> {
  return page.evaluate(() => {
    const start = performance.getEntriesByName('insights:start')[0]?.startTime ?? 0;
    return Object.fromEntries(performance.getEntriesByType('mark')
      .filter((m) => m.name.startsWith('insights:painted:'))
      .map((m) => [m.name.slice('insights:painted:'.length), Math.round(m.startTime - start)]));
  });
}

test.describe('P3 insights board (README §6 acceptance)', () => {
  test('with a 2 s delay injected into one source, the other four render first', async ({ page }) => {
    const errors = watchConsole(page);
    // The dev-only header (DEV_FAULT_INJECTION=true in the e2e stack) makes the server hold surveillance back.
    await page.route(/\/api\/insights\/surveillance/, (route) =>
      route.continue({ headers: { ...route.request().headers(), 'x-debug-delay-ms': '2000' } }));
    await page.goto('/insights');

    for (const s of sources.filter((x) => x !== 'surveillance'))
      await expect(tiles(page, s, 'ready')).toHaveCount(counts[s]);
    // The four are painted while surveillance is still a row of skeletons.
    await expect(tiles(page, 'surveillance', 'loading')).toHaveCount(counts.surveillance);
    await expect(tiles(page, 'surveillance', 'ready')).toHaveCount(counts.surveillance, { timeout: 10_000 });

    const t = await paintTimes(page);
    for (const s of sources.filter((x) => x !== 'surveillance')) expect(t[s]).toBeLessThan(t['surveillance']);
    expect(t['surveillance']).toBeGreaterThanOrEqual(2000);
    test.info().annotations.push({ type: 'paint ms (surveillance delayed 2 s)', description: JSON.stringify(t) });
    expect(errors).toEqual([]);
  });

  test('one source returning 500 shows exactly that source\'s error tiles; retry recovers it', async ({ page }) => {
    let fail = true;
    await page.route(/\/api\/insights\/pricing/, (route) => fail
      ? route.fulfill({ status: 500, contentType: 'application/problem+json', body: '{"title":"boom","status":500}' })
      : route.continue());
    await page.goto('/insights');

    await expect(tiles(page, 'pricing', 'error')).toHaveCount(counts.pricing);
    await expect(page.locator('[data-state=error]')).toHaveCount(counts.pricing);
    for (const s of sources.filter((x) => x !== 'pricing'))
      await expect(tiles(page, s, 'ready')).toHaveCount(counts[s]);

    fail = false;
    await page.getByTestId('retry-pricing').first().click();
    await expect(tiles(page, 'pricing', 'ready')).toHaveCount(counts.pricing);
    await expect(page.locator('[data-state=error]')).toHaveCount(0);
  });

  test('changing the as-of date cancels the calls in flight', async ({ page }) => {
    await page.goto('/insights');
    await expect(page.locator('[data-state=ready]')).toHaveCount(20);
    const dates = await page.getByTestId('as-of').locator('option').evaluateAll((os) => os.map((o) => (o as HTMLOptionElement).value));
    expect(dates.length).toBeGreaterThan(1);

    // Hold every source back, then switch the date while they're in flight.
    await page.route(/\/api\/insights\//, (route) =>
      route.continue({ headers: { ...route.request().headers(), 'x-debug-delay-ms': '1500' } }));
    const failed: Request[] = [];
    const started: Request[] = [];
    page.on('request', (r) => { if (r.url().includes('/api/insights/')) started.push(r); });
    page.on('requestfailed', (r) => { if (r.url().includes('/api/insights/')) failed.push(r); });

    await page.getByTestId('as-of').selectOption(dates[1]);
    await expect.poll(() => started.length).toBe(5);
    await page.getByTestId('as-of').selectOption(dates[0]);
    // The five requests for the superseded date are aborted by the client; the board shows the latest date.
    await expect.poll(() => failed.length, { timeout: 5000 }).toBe(5);
    expect(failed.every((r) => new URL(r.url()).searchParams.get('asOf') === dates[1])).toBe(true);
    await expect(page.locator('[data-state=ready]')).toHaveCount(20, { timeout: 10_000 });
    await expect(page.getByTestId('insights-caption')).toContainText(dates[0]);
  });

  test('budget: first tile and all tiles painted (README §10 P3, warm)', async ({ page }) => {
    await page.goto('/insights'); // warms the server cache for this scope (a MISS on a fresh server)
    await expect(page.locator('[data-state=ready]')).toHaveCount(20);
    const cold = Object.values(await paintTimes(page));
    console.log(`P3 first visit paint: first tile ${Math.min(...cold)} ms, all tiles ${Math.max(...cold)} ms`);
    await page.reload();
    await expect(page.locator('[data-state=ready]')).toHaveCount(20);
    const t = Object.values(await paintTimes(page));
    const first = Math.min(...t);
    const all = Math.max(...t);
    test.info().annotations.push({ type: 'P3 paint ms (warm)', description: `first ${first}, all ${all}` });
    console.log(`P3 warm paint: first tile ${first} ms, all tiles ${all} ms`);
    expect(first).toBeLessThan(500);
    expect(all).toBeLessThan(1500);
  });
});
