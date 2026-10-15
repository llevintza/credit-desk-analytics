import { expect, test } from './session';
import { watchConsole } from './helpers';

interface Deal { dealId: number; name: string; bondCount: number; repClass: string | null; }

test.describe('P4 deal explorer (README §6 acceptance)', () => {
  test('zero-bond deals are listed with blank bond columns, and their detail says so', async ({ page }) => {
    const errors = watchConsole(page);
    const listed = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/deals' && r.ok());
    await page.goto('/deals');
    const deals = ((await (await listed).json()) as { deals: Deal[] }).deals;
    const zeroBond = deals.filter((d) => d.bondCount === 0);
    expect(zeroBond.length).toBeGreaterThanOrEqual(3); // README §5.2
    await expect(page.getByTestId('deal-caption')).toContainText(`${deals.length} deals · ${zeroBond.length} without bonds yet`);

    for (const d of zeroBond) {
      await page.getByTestId('quick-filter').fill(d.name);
      const row = page.locator(`[data-testid=deal-grid] [role=row][row-id="${d.dealId}"]`);
      await expect(row).toBeVisible();
      await expect(row.locator('[col-id=bondCount]')).toHaveText('0');
      for (const col of ['currentBalance', 'repClass', 'repCusip', 'repCurrentBalance', 'repCoupon', 'heldMarketValue'])
        await expect(row.locator(`[col-id=${col}]`)).toHaveText('');
    }

    const last = zeroBond.at(-1)!;
    await page.locator(`[data-testid=deal-grid] [role=row][row-id="${last.dealId}"] [col-id=name]`).click();
    await expect(page.getByTestId('deal-name')).toHaveText(last.name);
    await expect(page.getByTestId('no-tranches')).toBeVisible();
    expect(errors).toEqual([]);
  });

  test('selecting a deal shows its capital structure, trend and positions; the list payload is reported', async ({ page }) => {
    const errors = watchConsole(page);
    const listed = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/deals' && r.ok());
    await page.goto('/deals');
    const res = await listed;
    const deals = ((await res.json()) as { deals: (Deal & { positions: number | null })[] }).deals;
    const held = deals.find((d) => d.bondCount > 2 && d.positions !== null)!;
    await page.getByTestId('quick-filter').fill(held.name);
    await page.locator(`[data-testid=deal-grid] [role=row][row-id="${held.dealId}"] [col-id=name]`).click();
    await expect(page.getByTestId('deal-name')).toHaveText(held.name);
    await expect(page.getByTestId('capital-stack').locator('li')).toHaveCount(held.bondCount);
    await expect(page.getByTestId('capital-stack').locator('li .label').first()).toHaveText(held.repClass!);
    await expect(page.getByTestId('trend-table')).toBeVisible();
    await expect(page.getByTestId('position-grid')).toBeVisible();

    const size = await page.evaluate(() => performance.getEntriesByType('resource')
      .filter((e) => new URL(e.name).pathname === '/api/deals')
      .map((e) => ({ encoded: (e as PerformanceResourceTiming).encodedBodySize, decoded: (e as PerformanceResourceTiming).decodedBodySize }))[0]);
    test.info().annotations.push({ type: 'GET /api/deals bytes (encoded / decoded)', description: JSON.stringify({ ...size, deals: deals.length }) });
    expect(errors).toEqual([]);
  });
});
