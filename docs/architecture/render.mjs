// Renders each SVG in this folder to a PNG beside it, at 2× (the README links the PNGs). Uses the e2e Playwright
// Chromium, so the output matches across machines up to font availability:
//   (cd e2e && npm ci && npx playwright install chromium) && node docs/architecture/render.mjs
import { createRequire } from 'node:module';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const { chromium } = createRequire(join(here, '../../e2e/package.json'))('@playwright/test');

const browser = await chromium.launch();
try {
  for (const file of readdirSync(here).filter((f) => f.endsWith('.svg'))) {
    const svg = readFileSync(join(here, file), 'utf8');
    const [, width, height] = svg.match(/<svg[^>]*\bwidth="(\d+)"[^>]*\bheight="(\d+)"/) ?? [];
    if (!width) throw new Error(`${file}: no width/height on the root <svg>`);
    const page = await browser.newPage({ viewport: { width: +width, height: +height }, deviceScaleFactor: 2 });
    await page.setContent(`<html><body style="margin:0">${svg}</body></html>`);
    const out = join(here, file.replace(/\.svg$/, '.png'));
    await page.locator('svg').first().screenshot({ path: out });
    await page.close();
    console.log(`${file} → ${out}`);
  }
} finally {
  await browser.close();
}
