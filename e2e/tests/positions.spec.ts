import { expect, test } from '@playwright/test';
import { gridReady, watchConsole, watchQueries } from './helpers';

test.describe('P1 positions (README §6 acceptance)', () => {
  test('scrolling to the last row shows it, and the pinned summary row reflects the filter', async ({ page }) => {
    const errors = watchConsole(page);
    await page.goto('/positions');
    await gridReady(page);
    const total = Number(/(\d+) total/.exec(await page.getByTestId('rows').innerText())![1]);
    expect(total).toBeGreaterThan(1000);

    // Scroll the grid body to the bottom until the infinite model has loaded and rendered the last row.
    await expect(async () => {
      await page.locator('.ag-body-vertical-scroll-viewport').evaluate((el) => { el.scrollTop = el.scrollHeight; });
      await expect(page.locator(`[row-index="${total - 1}"]`).first()).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 30_000 });

    // The summary row is pinned to the bottom; its label sits in the pinned-left identity column.
    await expect(page.locator('.summary-row').getByText('Total', { exact: true })).toBeVisible();
    expect(errors).toEqual([]);
  });

  test('switching preset re-requests once, with the new columns and nothing else', async ({ page }) => {
    await page.goto('/positions');
    await gridReady(page);
    await page.waitForTimeout(500);
    const q = watchQueries(page);

    await page.getByTestId('preset').selectOption('Surveillance');
    await expect.poll(() => q.finished.length).toBe(1);
    await page.waitForTimeout(800); // nothing else follows
    expect(q.finished.length).toBe(1);
    const body = q.bodies()[0];
    expect(body.startRow).toBe(0);
    expect(body.columns).toContain('cpr_1m');
    expect(body.columns).not.toContain('dv01');
    expect(body.quickFilter).toBeUndefined();
  });

  test('rapid typing in the quick filter results in exactly one completed request', async ({ page }) => {
    await page.goto('/positions');
    await gridReady(page);
    await page.waitForTimeout(500);
    const q = watchQueries(page);

    await page.keyboard.press('/'); // README §9.3: focus the quick filter
    await expect(page.getByTestId('quick-filter')).toBeFocused();
    await page.keyboard.type('clo 2024', { delay: 40 });
    await expect.poll(() => q.finished.length).toBe(1);
    await page.waitForTimeout(1_000);
    expect(q.finished.length).toBe(1);
    expect(q.bodies()[0].quickFilter).toBe('clo 2024');
  });

  test('CSV export downloads the current view from the API', async ({ page }) => {
    await page.goto('/positions');
    await gridReady(page);
    const download = page.waitForEvent('download');
    await page.keyboard.press('Control+Shift+E');
    const file = await download;
    expect(file.suggestedFilename()).toMatch(/^positions-\d{4}-\d{2}-\d{2}\.csv$/);
    const text = await (await file.createReadStream()).toArray().then((c) => Buffer.concat(c).toString('utf8'));
    expect(text.split('\n')[0]).toContain('Deal');
  });

  test('Alt+number switches pages and the theme toggle persists', async ({ page }) => {
    await page.goto('/positions');
    await gridReady(page);
    await page.keyboard.press('Alt+2');
    await expect(page).toHaveURL(/\/funds$/);
    await page.keyboard.press('Alt+1');
    await expect(page).toHaveURL(/\/positions$/);

    const html = page.locator('html');
    const before = await html.getAttribute('data-theme');
    await page.getByTestId('theme-toggle').click();
    await expect(html).not.toHaveAttribute('data-theme', before!);
    await page.reload();
    await expect(html).not.toHaveAttribute('data-theme', before!);
    await page.getByTestId('theme-toggle').click(); // leave it as it was
  });
});
