import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import * as path from 'node:path';
import { gridReady } from './helpers';

/**
 * README §9.1 / #51 AC6: contrast AA in both themes (#147). axe's color-contrast rule on the rendered positions page,
 * per theme and palette. The grid colours only negatives (--down), and axe never sees the hover background, so the
 * token check below covers every text token on every background in all four theme × palette combinations.
 */
const combos = [
  { theme: 'dark', palette: 'standard' },
  { theme: 'light', palette: 'standard' },
  { theme: 'light', palette: 'colorblind' },
] as const;

for (const { theme, palette } of combos) {
  test(`axe color-contrast: positions page, ${theme} + ${palette}`, async ({ page }) => {
    await page.goto('/positions');
    await page.evaluate((s) => localStorage.setItem('desk.settings', JSON.stringify(s)), { theme, palette, negatives: 'minus' });
    await page.reload();
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    await expect(page.locator('html')).toHaveAttribute('data-palette', palette);
    await gridReady(page);

    const results = await new AxeBuilder({ page }).withRules(['color-contrast']).analyze();
    expect(results.violations.flatMap((v) => v.nodes.map((n) => `${n.target.join(' ')}: ${n.failureSummary}`))).toEqual([]);
    // Not vacuous: axe measured the grid's text.
    expect(results.passes.find((p) => p.id === 'color-contrast')?.nodes.length ?? 0).toBeGreaterThan(0);
  });
}

test('tokens: every text token is at least 4.5:1 on every background, in every theme and palette', () => {
  // test.info().file rather than __dirname, which exists only while e2e compiles to CommonJS.
  const script = path.resolve(path.dirname(test.info().file), '../../web/scripts/contrast-check.mjs');
  const run = spawnSync(process.execPath, [script], { encoding: 'utf8' });
  // Status first, so a crash fails with the script's stderr; no fixed pair count, so adding a token doesn't break it.
  expect(run.status, run.stdout + run.stderr).toBe(0);
  expect(run.stdout).toMatch(/\b\d+ pairs, 0 below 4\.5:1/);
});
