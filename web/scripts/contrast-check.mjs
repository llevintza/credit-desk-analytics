#!/usr/bin/env node
// WCAG 2.x contrast of every text token on every background token, per theme and palette (#147, #51 AC6, #228).
// Reads the tokens from src/styles.scss, so it checks what ships. Grid figures are 12 px normal text: AA is 4.5:1.
// Usage: node web/scripts/contrast-check.mjs [path/to/styles.scss]   (exit 1 if any pair is below 4.5:1)
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const AA = 4.5;
const FOREGROUNDS = ['text', 'muted', 'accent', 'warn', 'up', 'down'];
const BACKGROUNDS = ['surface-1', 'surface-2', 'surface-3', 'zebra', 'hover'];
const SELECTORS = {
  dark: ':root',
  light: ":root[data-theme='light']",
  colorblind: ":root[data-palette='colorblind']",
  lightColorblind: ":root[data-theme='light'][data-palette='colorblind']",
};

/** The custom properties declared in the rule whose selector is exactly `selector`. */
export function tokens(scss, selector) {
  const out = {};
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const rule = new RegExp(`(?:^|\\n)${escaped}\\s*\\{([^}]*)\\}`).exec(scss);
  if (!rule) throw new Error(`No rule for ${selector}`);
  for (const [, name, value] of rule[1].matchAll(/--([\w-]+):\s*([^;]+);/g)) out[name] = value.trim();
  return out;
}

function luminance(hex) {
  const m = /^#([0-9a-f]{6})$/i.exec(hex);
  if (!m) throw new Error(`Not a #rrggbb colour: ${hex}`);
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(m[1].slice(i, i + 2), 16) / 255)
    .map((c) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function ratio(a, b) {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

/** The four theme × palette combinations, cascaded as the browser applies them. */
export function themes(scss) {
  const dark = tokens(scss, SELECTORS.dark);
  const light = { ...dark, ...tokens(scss, SELECTORS.light) };
  return {
    'dark': dark,
    'dark + colour-blind': { ...dark, ...tokens(scss, SELECTORS.colorblind) },
    'light': light,
    'light + colour-blind': { ...light, ...tokens(scss, SELECTORS.colorblind), ...tokens(scss, SELECTORS.lightColorblind) },
  };
}

export function check(scss) {
  const rows = [];
  for (const [theme, t] of Object.entries(themes(scss))) {
    for (const fg of FOREGROUNDS) {
      for (const bg of BACKGROUNDS) rows.push({ theme, fg, fgHex: t[fg], bg, bgHex: t[bg], ratio: ratio(t[fg], t[bg]) });
    }
  }
  return rows;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const file = process.argv[2] ?? fileURLToPath(new URL('../src/styles.scss', import.meta.url));
  const rows = check(readFileSync(file, 'utf8'));
  console.log(`theme                 fg     hex      bg         hex      ratio  AA ${AA}:1`);
  for (const r of rows) {
    console.log(`${r.theme.padEnd(21)} ${r.fg.padEnd(6)} ${r.fgHex}  ${r.bg.padEnd(10)} ${r.bgHex}  ${r.ratio.toFixed(2).padStart(5)}  ${r.ratio >= AA ? 'pass' : 'FAIL'}`);
  }
  const failed = rows.filter((r) => r.ratio < AA);
  const worst = rows.reduce((a, b) => (b.ratio < a.ratio ? b : a));
  console.log(`\n${rows.length} pairs, ${failed.length} below ${AA}:1; worst ${worst.ratio.toFixed(2)} (${worst.theme} ${worst.fg} on ${worst.bg})`);
  process.exit(failed.length ? 1 : 0);
}
