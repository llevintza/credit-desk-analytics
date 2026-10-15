import { expect, test } from './session';

const others = ['core', 'market', 'pricing', 'reference'] as const;

/**
 * #357: the P3 acceptance ("the other four render first") must hold whichever source reaches the server first.
 * insights.spec.ts can pass by luck when the delayed source happens to arrive last; here it always arrives first
 * (the other four are held 300 ms in the browser), so a per-user cap that queues them behind it fails every time.
 * Also the measurement for the ADR-0005 #357 amendment (paint ms in the annotation).
 */
test('P3: a slow source that arrives first does not hold the other four back (#357)', async ({ page }) => {
  await page.route(/\/api\/insights\/surveillance/, (route) =>
    route.continue({ headers: { ...route.request().headers(), 'x-debug-delay-ms': '2000' } }));
  await page.route(/\/api\/insights\/(core|market|pricing|reference)/, async (route) => {
    await new Promise((r) => setTimeout(r, 300));
    await route.continue();
  });
  await page.goto('/insights');
  await expect(page.locator('[data-source=surveillance][data-state=ready]')).toHaveCount(5, { timeout: 10_000 });

  const t = await page.evaluate(() => {
    const start = performance.getEntriesByName('insights:start')[0]?.startTime ?? 0;
    return Object.fromEntries(performance.getEntriesByType('mark')
      .filter((m) => m.name.startsWith('insights:painted:'))
      .map((m) => [m.name.slice('insights:painted:'.length), Math.round(m.startTime - start)]));
  });
  test.info().annotations.push({ type: 'paint ms (surveillance first, delayed 2 s)', description: JSON.stringify(t) });
  expect(t['surveillance']).toBeGreaterThanOrEqual(2000);
  for (const s of others) expect(t[s]).toBeLessThan(t['surveillance']);
});
