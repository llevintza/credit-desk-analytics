import { Page, Request, Response } from '@playwright/test';
import { expect, test } from './session';
import { mkdirSync, writeFileSync } from 'node:fs';
import { gridReady } from './helpers';

/**
 * ADR-0014 measurement (#67): one demo session, every page twice, with the production limits on.
 * Opt-in like perf.spec.ts (playwright.config.ts collects it only when DEMO_SESSION is set):
 *   DEMO_SESSION=1 BASE_URL=… DESK_EMAIL=… DESK_PASSWORD=… npx playwright test demo-session
 * Run it against a stack without e2e/docker-compose.e2e.yml, so the per-user limits are the production ones.
 * THINK_MS (default 2000) is the pause between a user's actions; 0 is a user clicking as fast as the pages paint.
 * It writes every /api response (path, status, X-Cache, body bytes, start and end; an aborted request has status 0)
 * to test-results/demo-session.json, which perf/demo-session-report.mjs turns into the ADR-0014 numbers, and asserts
 * what README §7.2 promises: the second view of every page is served from the cache (no MISS), and nothing in the
 * session is refused with a 429.
 */
test.skip(!process.env.DEMO_SESSION, 'measurement run: set DEMO_SESSION=1');

const think = Number(process.env.THINK_MS ?? 2000);

interface Call {
  pass: string;
  method: string;
  path: string;
  status: number;
  cache: string | null;
  bytes: number;
  start: number;
  end: number;
}

async function pause(page: Page): Promise<void> {
  if (think > 0) await page.waitForTimeout(think);
}

async function positions(page: Page): Promise<void> {
  await page.locator('a[href="/positions"]').click();
  await gridReady(page);
  await pause(page);
  // Scroll a few screens down the book, as a trader skimming it would. Absolute positions, so both passes ask for the
  // same blocks however fast the previous scroll settled.
  for (let i = 1; i <= 3; i++) {
    await page.locator('.ag-body-vertical-scroll-viewport').evaluate((el, screens) => { el.scrollTop = el.clientHeight * screens; }, 8 * i);
    await page.waitForLoadState('networkidle');
    await pause(page);
  }
  await page.locator('.ag-body-vertical-scroll-viewport').evaluate((el) => { el.scrollTop = 0; });
  await gridReady(page);
  for (const preset of ['Surveillance', 'All', 'Risk']) {
    await page.getByTestId('preset').selectOption(preset);
    await gridReady(page);
    await pause(page);
  }
}

async function funds(page: Page): Promise<void> {
  await page.locator('a[href="/funds"]').click();
  await expect(page.getByTestId('caption')).toContainText('month');
  await pause(page);
  for (const range of ['QTD', '1Y', 'ITD', 'YTD']) {
    await page.getByTestId(`range-${range}`).click();
    await expect(page.getByTestId('caption')).toContainText(range);
    await pause(page);
  }
}

async function insights(page: Page): Promise<void> {
  await page.locator('a[href="/insights"]').click();
  await expect(page.locator('[data-state=ready]')).toHaveCount(20);
  await pause(page);
}

async function deals(page: Page): Promise<void> {
  await page.locator('a[href="/deals"]').click();
  const rows = page.locator('[data-testid=deal-grid] [role=row][row-id]');
  await expect(rows.first()).toBeVisible();
  await pause(page);
  for (let i = 0; i < 3; i++) {
    const name = rows.nth(i).locator('[col-id=name]');
    const dealName = (await name.innerText()).trim();
    await name.click();
    await expect(page.getByTestId('deal-name')).toHaveText(dealName);
    await page.waitForLoadState('networkidle');
    await pause(page);
  }
}

test('demo session: second views are cache hits, nothing is refused', async ({ page }) => {
  test.setTimeout(600_000);
  const calls: Call[] = [];
  const t0 = Date.now();
  let pass = 'first';
  // The pass a request belongs to is the one running when it was sent, not when its body finished reading.
  const sentIn = new WeakMap<Request, string>();
  page.on('request', (req) => sentIn.set(req, pass));
  page.on('response', async (res: Response) => {
    const url = new URL(res.url());
    if (!url.pathname.startsWith('/api/')) return;
    const timing = res.request().timing();
    const body = res.status() === 200 ? await res.body().catch(() => Buffer.alloc(0)) : Buffer.alloc(0);
    calls.push({
      pass: sentIn.get(res.request()) ?? pass,
      method: res.request().method(),
      path: url.pathname,
      status: res.status(),
      cache: res.headers()['x-cache'] ?? null,
      bytes: body.length,
      start: Math.round(timing.startTime - t0),
      end: Math.round(timing.startTime + timing.responseEnd - t0),
    });
  });
  // A request the page aborted (a superseded block, switchMap) never gets a response, but it spent a token.
  page.on('requestfailed', (req) => {
    const url = new URL(req.url());
    if (!url.pathname.startsWith('/api/')) return;
    const timing = req.timing();
    calls.push({ pass: sentIn.get(req) ?? pass, method: req.method(), path: url.pathname, status: 0, cache: null, bytes: 0, start: Math.round(timing.startTime - t0), end: Math.round(Date.now() - t0) });
  });

  try {
    await page.goto('/positions');
    await gridReady(page);
    for (pass of ['first', 'repeat']) {
      await positions(page);
      await funds(page);
      await insights(page);
      await deals(page);
    }
    await page.waitForLoadState('networkidle');
  } finally {
    // Written even when a page fails to paint, so the refused or failed calls can be read.
    mkdirSync('test-results', { recursive: true });
    writeFileSync('test-results/demo-session.json', JSON.stringify({ thinkMs: think, calls }, null, 1));
  }
  const summary = ['first', 'repeat'].map((p) => {
    const mine = calls.filter((c) => c.pass === p);
    const by = (k: string) => mine.filter((c) => c.cache === k).length;
    return {
      pass: p,
      requests: mine.length,
      miss: by('MISS'),
      hit: by('HIT'),
      notModified: mine.filter((c) => c.status === 304).length,
      refused: mine.filter((c) => c.status === 429).length,
      missBytes: mine.filter((c) => c.cache === 'MISS').reduce((n, c) => n + c.bytes, 0),
      seconds: Math.round((Math.max(...mine.map((c) => c.end)) - Math.min(...mine.map((c) => c.start))) / 100) / 10,
    };
  });
  console.log(`DEMO_SESSION think=${think}ms ${JSON.stringify(summary)}`);

  expect(calls.filter((c) => c.status === 429), 'requests refused with 429').toEqual([]);
  expect(calls.filter((c) => c.pass === 'repeat' && c.cache === 'MISS'), 'second views that read the database').toEqual([]);
});
