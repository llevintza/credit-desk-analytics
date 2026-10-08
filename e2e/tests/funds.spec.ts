import { expect, test } from '@playwright/test';
import { watchConsole } from './helpers';

test.describe('P2 fund performance (README §6 acceptance)', () => {
  test('flipping ranges quickly never shows a stale range', async ({ page }) => {
    const errors = watchConsole(page);
    await page.goto('/funds');
    await expect(page.getByTestId('caption')).toContainText('YTD');
    const completed: string[] = [];
    page.on('requestfinished', (r) => { if (r.url().includes('/performance')) completed.push(new URL(r.url()).searchParams.get('range') ?? ''); });

    for (const range of ['QTD', '1Y', 'ITD', 'QTD', 'ITD']) await page.getByTestId(`range-${range}`).click();
    await expect(page.getByTestId('caption')).toContainText('ITD');
    await page.waitForTimeout(800); // nothing older lands afterwards
    await expect(page.getByTestId('caption')).toContainText('ITD');
    const months = Number(/(\d+) months?/.exec((await page.getByTestId('caption').innerText()))![1]);
    // One column per month plus the pinned label (virtualised, so read the grid's ARIA column count).
    await expect(page.locator('[data-testid=fund-grid] [aria-colcount]').first()).toHaveAttribute('aria-colcount', String(months + 1));
    expect(completed.at(-1)).toBe('ITD');
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
