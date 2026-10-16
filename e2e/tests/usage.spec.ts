import { expect, test } from './session';

/** #278 (from #70): the viewer account has no Usage page; the route guard sends it back to Positions. */
test('a viewer has no Usage entry, and /usage sends it to Positions', async ({ page }) => {
  await page.goto('/positions');
  await expect(page.getByTestId('positions-grid')).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Pages' }).getByRole('link', { name: /Usage/ })).toHaveCount(0);
  await page.goto('/usage');
  await expect(page).toHaveURL(/\/positions$/);
  await expect(page.getByTestId('usage')).toHaveCount(0);
});
