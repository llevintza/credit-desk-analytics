import { CDPSession, Page } from '@playwright/test';
import { expect, test } from './session';
import { mkdirSync, writeFileSync } from 'node:fs';
import { gridReady } from './helpers';

/**
 * ADR-0009 and README §10 measurements (run with PERF=1; playwright.config.ts only collects this file when PERF
 * is set, so the default e2e run neither lists nor skips it):
 *   PERF=1 BASE_URL=… DESK_EMAIL=… DESK_PASSWORD=… npx playwright test perf
 * Needs the API's per-user rate limit raised for the client-side run (it fetches every block back to back).
 */
test.skip(!process.env.PERF, 'measurement run: set PERF=1');

const runs = 5;

async function heapMb(cdp: CDPSession): Promise<number> {
  await cdp.send('HeapProfiler.collectGarbage');
  const { usedSize } = await cdp.send('Runtime.getHeapUsage');
  return usedSize / 1024 / 1024;
}

const median = (xs: number[]) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length / 2)];

/**
 * A bearer token for the page's own fetches (ADR-0024): refreshes with the tab's refresh token and puts the renewed
 * one back, as the app would. Same origin, so it is the API the page itself talks to.
 */
async function bearer(page: Page): Promise<string> {
  return page.evaluate(async () => {
    const res = await fetch('/api/auth/refresh', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ refreshToken: sessionStorage.getItem('desk.refresh') }),
    });
    const tokens = await res.json();
    sessionStorage.setItem('desk.refresh', tokens.refreshToken);
    return `Bearer ${tokens.accessToken}`;
  });
}

async function firstRows(page: Page): Promise<number> {
  return page.evaluate(() => performance.getEntriesByName('positions:first-rows')[0]?.startTime ?? -1);
}

test('first rows painted (warm) and memory: infinite row model vs loading every row client-side', async ({ page }) => {
  test.setTimeout(300_000);
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('HeapProfiler.enable');

  // Warm-up: API caches, HTTP cache, JIT.
  await page.goto('/positions');
  await gridReady(page);

  // 1. Infinite Row Model (what ships): navigation start → first rows rendered, then heap with the first view.
  const paint: number[] = [];
  for (let i = 0; i < runs; i++) {
    await page.goto('/positions');
    await gridReady(page);
    await expect.poll(() => firstRows(page)).toBeGreaterThan(0);
    paint.push(await firstRows(page));
  }
  const infiniteHeap = await heapMb(cdp);

  // 2. Client-side model's floor: every row of a preset in memory as row objects before anything paints.
  const clientSideFloor: Record<string, unknown> = {};
  for (const preset of ['Risk', 'All']) {
  const authorization = await bearer(page);
  const columns: string[] = await page.evaluate(async ([name, auth]) =>
    (await (await fetch('/api/presets/positions', { headers: { authorization: auth } })).json()).find((p: { name: string }) => p.name === name).state.columns, [preset, authorization] as const);
  const before = await heapMb(cdp);
  const clientSide = await page.evaluate(async ([cols, auth]) => {
    const started = performance.now();
    const rows: Record<string, unknown>[] = [];
    let total = Infinity;
    let bytes = 0;
    for (let start = 0; start < total; start += 500) {
      const res = await fetch('/api/positions/query', {
        method: 'POST',
        headers: { 'content-type': 'application/json', authorization: auth },
        body: JSON.stringify({ startRow: start, endRow: start + 500, columns: cols }),
      });
      const text = await res.text();
      bytes += text.length;
      const block = JSON.parse(text);
      total = block.rowCount;
      for (let r = 0; r < block.data[0].length; r++) {
        const row: Record<string, unknown> = {};
        for (let c = 0; c < block.columns.length; c++) row[block.columns[c]] = block.data[c][r];
        rows.push(row);
      }
    }
    (window as unknown as { keep: unknown }).keep = rows; // keep them alive for the heap reading
    return { ms: performance.now() - started, rows: rows.length, requests: Math.ceil(rows.length / 500), rawMb: bytes / 1024 / 1024 };
  }, [columns, authorization] as const);
  const clientHeap = (await heapMb(cdp)) - before;
  clientSideFloor[preset] = { columns: columns.length + 1, allRowsInMemoryMs: Math.round(clientSide.ms), rows: clientSide.rows, requests: clientSide.requests, rawPayloadMb: +clientSide.rawMb.toFixed(1), extraHeapMb: +clientHeap.toFixed(1) };
  await page.evaluate(() => { delete (window as unknown as { keep?: unknown }).keep; });
  }

  const result = {
    runs,
    infinite: { firstRowsPaintedMs: { median: Math.round(median(paint)), all: paint.map(Math.round) }, heapMbAfterFirstView: +infiniteHeap.toFixed(1) },
    clientSideFloor,
  };
  mkdirSync('results', { recursive: true });
  writeFileSync('results/perf.json', JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
  expect(median(paint)).toBeLessThan(1000); // README §10: first rows painted < 1.0 s warm
});

/**
 * #338 / ADR-0005: the fund page's cold first paint under the per-user concurrency cap. Each round clears the API's
 * caches (needs an admin account), then times navigation start → the first fund-grid row, read in the page on the
 * frame it appears. Run it with RATE_LIMIT_PER_USER_CONCURRENCY at its default of 1, and raised, to compare.
 */
test('fund page first paint, cold API caches', async ({ page }) => {
  test.setTimeout(300_000);
  const fundRow = '[data-testid=fund-grid] .ag-row';
  // Warm-up: HTTP cache and JIT (the API caches are cleared before every measured round).
  await page.goto('/funds');
  await expect(page.locator(fundRow).first()).toBeVisible();

  const paint: number[] = [];
  for (let i = 0; i < runs * 4; i++) {
    const authorization = await bearer(page);
    const cleared = await page.evaluate(async (auth) =>
      (await fetch('/api/admin/cache/clear', { method: 'POST', headers: { authorization: auth } })).status, authorization);
    expect(cleared, 'cache clear needs an admin account').toBe(204);
    await page.goto('/funds');
    const at = await page.waitForFunction((sel) => document.querySelector(sel) ? performance.now() : 0, fundRow, { polling: 'raf' });
    paint.push(await at.jsonValue() as number);
  }
  const sorted = [...paint].sort((a, b) => a - b);
  const result = {
    runs: paint.length,
    fundFirstPaintColdMs: { p50: Math.round(median(paint)), p95: Math.round(sorted[Math.ceil(0.95 * sorted.length) - 1]), max: Math.round(sorted[sorted.length - 1]), all: paint.map(Math.round) },
  };
  mkdirSync('results', { recursive: true });
  writeFileSync('results/perf-funds.json', JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
});
