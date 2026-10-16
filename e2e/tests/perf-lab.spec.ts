import { Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { expect, test } from './session';

/**
 * ADR-0028 numbers, measured by the Performance Lab itself (README §6 P5). Opt-in like perf.spec.ts (PERF=1;
 * playwright.config.ts only collects it then). Sign in as an admin so the cache can be switched off, against a
 * local stack whose dev knobs are on (the e2e compose override), never Render, Neon or production:
 *   PERF=1 BASE_URL=http://localhost:8080 DESK_EMAIL=<local admin> DESK_PASSWORD=… npx playwright test perf-lab
 * Each scenario runs once to warm up, then `runs` times; the table is the median of each column. The Lab's own
 * "copy as markdown" of the last 20 runs is saved next to it.
 */
test.skip(!process.env.PERF, 'measurement run: set PERF=1');

const runs = 5;

type Toggles = Partial<Record<'target' | 'format' | 'block' | 'columns' | 'cache' | 'encoding' | 'p3', string>>;

const scenarios: Toggles[] = [
  ...(['rows', 'columnar', 'msgpack'] as const).flatMap((format) =>
    (['displayed', 'all'] as const).map((columns): Toggles => ({ target: 'p1', format, columns, block: '200', cache: 'off', encoding: 'default' }))),
  { target: 'p1', format: 'columnar', columns: 'displayed', block: '100', cache: 'off', encoding: 'default' },
  { target: 'p1', format: 'columnar', columns: 'displayed', block: '500', cache: 'off', encoding: 'default' },
  { target: 'p1', format: 'columnar', columns: 'displayed', block: '200', cache: 'off', encoding: 'gzip' },
  { target: 'p1', format: 'columnar', columns: 'displayed', block: '200', cache: 'off', encoding: 'identity' },
  { target: 'p1', format: 'columnar', columns: 'displayed', block: '200', cache: 'on', encoding: 'default' },
  { target: 'p3', p3: 'per-source', cache: 'off', encoding: 'default' },
  { target: 'p3', p3: 'naive', cache: 'off', encoding: 'default' },
  { target: 'p3', p3: 'per-source', cache: 'on', encoding: 'default' },
  { target: 'p3', p3: 'naive', cache: 'on', encoding: 'default' },
];

async function apply(page: Page, t: Toggles): Promise<void> {
  for (const [key, value] of Object.entries(t)) {
    const id = key === 'target' ? `target-${value}` : key === 'p3' ? `p3-${value}` : `${key}-${value}`;
    await page.getByTestId(id).click();
  }
}

let next = 1;

async function runOnce(page: Page): Promise<string[]> {
  const id = next++;
  await page.getByTestId('run').click();
  await expect(page.getByTestId(`run-${id}`)).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('run')).toBeEnabled();
  return page.getByTestId(`run-${id}`).locator('td').allInnerTexts();
}

const median = (xs: number[]) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length / 2)];

/** The median of each numeric cell; text cells (and n/a) are taken from the last run. */
function medianRow(rows: string[][]): string[] {
  return rows[0].map((_, c) => {
    const values = rows.map((r) => r[c]);
    if (c === 0) return '';
    if (c === 2) return values.at(-1)!; // the request count is fixed per scenario
    if (c === 12 && values.every((v) => /^[\d.]+ \/ [\d.]+ \/ [\d.]+$/.test(v))) {
      const parts = values.map((v) => v.split(' / ').map(Number));
      return [0, 1, 2].map((i) => median(parts.map((p) => p[i])).toFixed(1)).join(' / ');
    }
    if (values.every((v) => v !== '' && Number.isFinite(Number(v)))) return median(values.map(Number)).toFixed(1);
    return values.at(-1)!;
  });
}

test('Performance Lab scenario matrix (ADR-0028)', async ({ page, context }) => {
  test.setTimeout(10 * 60_000);
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.goto('/lab');
  await expect(page.getByTestId('run')).toBeEnabled();
  await expect(page.getByTestId('cache-off'), 'sign in as a local admin to measure with the cache off').toBeEnabled();
  const headers = (await page.locator('[data-testid=results] thead th').allInnerTexts()).map((h) => h.trim());

  const lines = [`| ${headers.slice(1).join(' | ')} |`, `|${headers.slice(1).map(() => '---').join('|')}|`];
  for (const s of scenarios) {
    await apply(page, s);
    await runOnce(page); // warm-up: connection, JIT, the decoder's first load
    const rows: string[][] = [];
    for (let i = 0; i < runs; i++) rows.push(await runOnce(page));
    lines.push(`| ${medianRow(rows).slice(1).join(' | ')} |`);
  }

  await page.getByTestId('copy').click();
  await expect(page.getByTestId('copied')).toBeVisible();
  const last20 = await page.evaluate(() => navigator.clipboard.readText());
  mkdirSync('results', { recursive: true });
  const table = `Median of ${runs} runs per scenario (after one warm-up), ${new Date().toISOString()}, ${process.env.BASE_URL}\n\n${lines.join('\n')}\n`;
  writeFileSync('results/perf-lab.md', table);
  writeFileSync('results/perf-lab-last20.md', last20);
  console.log(table);
});
