import { expect, test } from '@playwright/test';

/**
 * #253 / #316: the As-of label is visually hidden by .sr-only but still names the select. jsdom does no layout, so the
 * unit spec (web/src/styles.spec.ts) can't prove the box is really 1×1 and clipped; this checks it in a real browser,
 * in both themes.
 */
for (const theme of ['dark', 'light'] as const) {
  test(`.sr-only As-of label: 1×1 clipped box, still announced (${theme})`, async ({ page }) => {
    await page.goto('/positions');
    await page.evaluate((s) => localStorage.setItem('desk.settings', JSON.stringify(s)), { theme, palette: 'standard', negatives: 'minus' });
    await page.reload();
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);

    const label = page.locator('header .field .sr-only');
    await expect(label).toHaveText('As-of date');
    const s = await label.evaluate((e) => {
      const c = getComputedStyle(e);
      const r = e.getBoundingClientRect();
      return {
        position: c.position, clipPath: c.clipPath, clip: c.clip, margin: c.margin, border: c.borderWidth, padding: c.padding,
        overflow: c.overflow, whiteSpace: c.whiteSpace, width: r.width, height: r.height, display: c.display, visibility: c.visibility,
      };
    });
    expect(s).toMatchObject({
      position: 'absolute', clipPath: 'inset(50%)', clip: 'rect(0px, 0px, 0px, 0px)', margin: '-1px', border: '0px', padding: '0px',
      overflow: 'hidden', whiteSpace: 'nowrap', width: 1, height: 1,
    });
    expect(s.display).not.toBe('none');
    expect(s.visibility).not.toBe('hidden');
    await expect(page.getByLabel('As-of date', { exact: true })).toHaveJSProperty('tagName', 'SELECT');
  });
}
