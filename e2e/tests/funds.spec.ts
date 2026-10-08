import { expect, test } from '@playwright/test';
import { watchConsole } from './helpers';

test.describe('P2 fund performance (README §6 acceptance)', () => {
  test('flipping ranges quickly never shows a stale range', async ({ page }) => {
    const errors = watchConsole(page);
    // Hold back the ranges that get superseded, so their responses would land after ITD's if anything let them.
    await page.route(/\/performance\?.*range=(QTD|1Y)/, async (route) => {
      await new Promise((resolve) => setTimeout(resolve, 1500));
      await route.continue().catch(() => undefined); // the SPA may have cancelled it meanwhile
    });
    await page.goto('/funds');
    await expect(page.getByTestId('caption')).toContainText('YTD');
    const settled: string[] = [];
    const track = (r: { url(): string }) => { if (r.url().includes('/performance')) settled.push(new URL(r.url()).searchParams.get('range') ?? ''); };
    page.on('requestfinished', track);
    page.on('requestfailed', track);

    for (const range of ['QTD', '1Y', 'ITD']) await page.getByTestId(`range-${range}`).click();
    await expect(page.getByTestId('caption')).toContainText('ITD');
    // Every delayed request has settled (answered or cancelled); ITD is still what's on screen.
    await expect.poll(() => [...settled].sort(), { timeout: 5000 }).toEqual(['1Y', 'ITD', 'QTD']);
    await expect(page.getByTestId('caption')).toContainText('ITD');
    const months = Number(/(\d+) months?/.exec((await page.getByTestId('caption').innerText()))![1]);
    // One column per month plus the pinned label (virtualised, so read the grid's ARIA column count).
    await expect(page.locator('[data-testid=fund-grid] [aria-colcount]').first()).toHaveAttribute('aria-colcount', String(months + 1));
    expect(errors).toEqual([]);
  });

  test('a fund with a mid-month inception starts at its first month-end', async ({ page }) => {
    await page.goto('/funds');
    await page.getByTestId('fund').selectOption({ label: 'Residential Credit Fund' });
    await page.getByTestId('range-ITD').click();
    await expect(page.getByTestId('caption')).toContainText('Residential Credit Fund · ITD');
    // Inception 2023-03-15 (README §5.2 edge case) → the first column is the March 2023 month-end.
    await expect(page.locator('[data-testid=fund-grid] .ag-header-cell[col-id="2023-03-31"]')).toBeVisible();
    await expect(page.locator('[data-testid=fund-grid] .ag-header-cell[col-id="2023-02-28"]')).toHaveCount(0);
  });
});
