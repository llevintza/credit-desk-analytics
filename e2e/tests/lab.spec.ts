import { Page } from '@playwright/test';
import { expect, test } from './session';
import { watchConsole } from './helpers';

/** The cells of the newest run in the results table (README §6 P5), by column header. */
async function newestRun(page: Page): Promise<Record<string, string>> {
  const headers = await page.locator('[data-testid=results] thead th').allInnerTexts();
  const cells = await page.locator('[data-testid=results] tbody tr').first().locator('td').allInnerTexts();
  return Object.fromEntries(headers.map((h, i) => [h.trim(), cells[i]?.trim() ?? '']));
}

/** Presses Run and waits for the new row: run ids count up from 1. */
async function run(page: Page, id: number): Promise<Record<string, string>> {
  await page.getByTestId('run').click();
  await expect(page.getByTestId(`run-${id}`)).toBeVisible();
  await expect(page.getByTestId('run')).toBeEnabled();
  return newestRun(page);
}

const kb = (cell: string) => Number(cell);

test.describe('P5 Performance Lab (README §6 acceptance: all toggles work, numbers from real requests)', () => {
  test('each payload format, block size and column set is a real request with its own bytes and timings', async ({ page }) => {
    const errors = watchConsole(page);
    const sent: { accept: string | null; body: { endRow: number; columns: string[] } }[] = [];
    page.on('request', (r) => {
      if (r.url().includes('/api/positions/query?lab=')) sent.push({ accept: r.headers()['accept'] ?? null, body: r.postDataJSON() });
    });
    await page.goto('/lab');
    await expect(page.getByTestId('run')).toBeEnabled();

    const columnar = await run(page, 1);
    expect(columnar['Scenario']).toMatch(/^P1 · columnar JSON · 200 rows · displayed \d+ cols · cache on$/);
    expect(columnar['Rows × cols']).toMatch(/^200 × \d+$/);
    for (const h of ['Wire KB', 'Decoded KB', 'TTFB ms', 'Download ms', 'Parse ms', 'Render ms', 'Total ms'])
      expect(Number.isFinite(Number(columnar[h])), `${h} = ${columnar[h]}`).toBe(true);
    expect(kb(columnar['Wire KB'])).toBeGreaterThan(0);
    expect(kb(columnar['Wire KB'])).toBeLessThan(kb(columnar['Decoded KB'])); // compressed on the wire
    expect(columnar['Server db / ser / total ms']).toMatch(/^[\d.]+ \/ [\d.]+ \/ [\d.]+$/);
    expect(columnar['Cache']).toMatch(/^(MISS|HIT)$/);

    await page.getByTestId('format-rows').click();
    const rows = await run(page, 2);
    expect(rows['Scenario']).toContain('row JSON');
    // ADR-0007: row objects repeat every column name, so the same block is larger, raw and compressed.
    expect(kb(rows['Decoded KB'])).toBeGreaterThan(kb(columnar['Decoded KB']) * 1.5);
    expect(kb(rows['Wire KB'])).toBeGreaterThan(kb(columnar['Wire KB']));

    await page.getByTestId('format-msgpack').click();
    const msgpack = await run(page, 3);
    expect(msgpack['Scenario']).toContain('MessagePack');
    expect(msgpack['Rows × cols']).toBe(columnar['Rows × cols']);
    expect(kb(msgpack['Wire KB'])).toBeLessThan(kb(msgpack['Decoded KB'])); // compressed too (ADR-0028)

    await page.getByTestId('format-columnar').click();
    await page.getByTestId('block-500').click();
    await page.getByTestId('columns-all').click();
    const all = await run(page, 4);
    expect(all['Rows × cols']).toMatch(/^500 × (19\d|2\d\d)$/);
    expect(kb(all['Wire KB'])).toBeGreaterThan(kb(columnar['Wire KB']));

    await page.getByTestId('block-100').click();
    expect((await run(page, 5))['Rows × cols']).toMatch(/^100 × /);

    expect(sent.map((s) => [s.accept, s.body.endRow])).toEqual([
      ['application/json', 200], ['application/vnd.desk.rows+json', 200], ['application/x-msgpack', 200],
      ['application/json', 500], ['application/json', 100],
    ]);
    expect(sent[3].body.columns.length).toBeGreaterThan(sent[0].body.columns.length);
    expect(errors).toEqual([]);
  });

  test('the e2e stack honours the forced encoding (dev knobs on); a viewer cannot switch the cache off', async ({ page }) => {
    await page.goto('/lab');
    await expect(page.getByTestId('run')).toBeEnabled();
    await expect(page.getByTestId('cache-off')).toBeDisabled();

    await page.getByTestId('encoding-identity').click();
    const none = await run(page, 1);
    expect(none['Encoding']).toBe('none');
    expect(none['Wire KB']).toBe(none['Decoded KB']);

    await page.getByTestId('encoding-gzip').click();
    const gzip = await run(page, 2);
    expect(gzip['Encoding']).toBe('gzip');
    expect(kb(gzip['Wire KB'])).toBeLessThan(kb(gzip['Decoded KB']));

    await page.getByTestId('encoding-br').click();
    const br = await run(page, 3);
    expect(br['Encoding']).toBe('br');
    expect(kb(br['Wire KB'])).toBeLessThanOrEqual(kb(gzip['Wire KB']));
  });

  test('P3: naive sends 20 calls, per-source 5, each measured', async ({ page }) => {
    const calls: string[] = [];
    page.on('request', (r) => { if (r.url().includes('/api/insights/') && r.url().includes('lab=')) calls.push(r.url()); });
    await page.goto('/lab');
    await page.getByTestId('target-p3').click();
    await expect(page.getByTestId('run')).toBeEnabled();

    const five = await run(page, 1);
    expect(five['Req']).toBe('5');
    expect(calls).toHaveLength(5);
    expect(kb(five['Wire KB'])).toBeGreaterThan(0);

    await page.getByTestId('p3-naive').click();
    const twenty = await run(page, 2);
    expect(twenty['Scenario']).toBe('P3 · naive 20 calls · cache on');
    expect(twenty['Req']).toBe('20');
    expect(calls).toHaveLength(25);
    expect(calls.slice(5).every((u) => new URL(u).searchParams.has('grid'))).toBe(true);
    expect(twenty['Cache']).toMatch(/HIT|MISS/);
  });

  test('keeps the last 20 runs and copies them as a markdown table', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await page.goto('/lab');
    await expect(page.getByTestId('run')).toBeEnabled();
    for (let id = 1; id <= 21; id++) await run(page, id);
    await expect(page.locator('[data-testid=results] tbody tr')).toHaveCount(20);
    await expect(page.getByTestId('run-1')).toHaveCount(0);

    await page.getByTestId('copy').click();
    await expect(page.getByTestId('copied')).toHaveText('Copied 20 runs as markdown.');
    const md = await page.evaluate(() => navigator.clipboard.readText());
    const lines = md.trimEnd().split('\n');
    expect(lines).toHaveLength(22);
    expect(lines[0]).toMatch(/^\| # \| Scenario \| Req \|/);
    expect(lines[2]).toMatch(/^\| 2 \| P1 · columnar JSON/);
    expect(lines[21]).toMatch(/^\| 21 \|/);
  });
});
