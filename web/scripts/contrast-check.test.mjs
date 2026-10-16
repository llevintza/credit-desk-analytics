// node:test for contrast-check.mjs (#229): the token parser, the WCAG maths, the theme cascade and the exit code,
// plus the shipped styles.scss, so reverting the #214 light-theme tokens fails here and not only in e2e.
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { after, test } from 'node:test';
import assert from 'node:assert/strict';
import { check, ratio, themes, tokens } from './contrast-check.mjs';

const script = join(import.meta.dirname, 'contrast-check.mjs');
const shipped = readFileSync(join(import.meta.dirname, '../src/styles.scss'), 'utf8');
const dirs = [];
after(() => dirs.forEach((d) => rmSync(d, { recursive: true, force: true })));

function run(...args) {
  return spawnSync(process.execPath, [script, ...args], { encoding: 'utf8' });
}

function file(scss) {
  const dir = mkdtempSync(join(tmpdir(), 'contrast-check-'));
  dirs.push(dir);
  writeFileSync(join(dir, 'styles.scss'), scss);
  return join(dir, 'styles.scss');
}

// The light-theme tokens #214 replaced: --up #11804d → #0f7344 (light), --down #b25600 → #a14e00 (light + colour-blind).
const reverted214 = shipped.replace('--up: #0f7344;', '--up: #11804d;').replace('--down: #a14e00;', '--down: #b25600;');

// A minimal stylesheet: every foreground #000000 and every background #ffffff (21:1), with each override rule present.
const fgs = ['text', 'muted', 'accent', 'warn', 'up', 'down'];
const bgs = ['surface-1', 'surface-2', 'surface-3', 'zebra', 'hover'];
const decl = (names, hex) => names.map((n) => `--${n}: ${hex};`).join(' ');
const minimal = (overrides = {}) => [
  `:root { color-scheme: dark; ${decl(fgs, '#000000')} ${decl(bgs, '#ffffff')} }`,
  `:root[data-theme='light'] { ${overrides.light ?? '--text: #111111;'} }`,
  `:root[data-palette='colorblind'] { ${overrides.colorblind ?? '--up: #222222; --down: #333333;'} }`,
  `:root[data-theme='light'][data-palette='colorblind'] { ${overrides.lightColorblind ?? '--down: #444444;'} }`,
].join('\n');

test('tokens() reads the custom properties of the rule whose selector matches exactly', () => {
  const scss = minimal();
  assert.equal(tokens(scss, ':root').text, '#000000');
  assert.equal(tokens(scss, ':root')['color-scheme'], undefined);
  assert.deepEqual(tokens(scss, ":root[data-theme='light']"), { text: '#111111' });
  // ':root' must not pick up the attribute-qualified rules, nor they each other.
  assert.deepEqual(tokens(scss, ":root[data-theme='light'][data-palette='colorblind']"), { down: '#444444' });
});

test('tokens() trims values and ignores declarations that are not custom properties', () => {
  assert.deepEqual(tokens(':root {\n  --a:   #aabbcc  ;\n  color: red;\n  --b-c: 1px;\n}', ':root'), { a: '#aabbcc', 'b-c': '1px' });
});

test('tokens() throws when no rule has that selector', () => {
  assert.throws(() => tokens(':root { --text: #000000; }', ":root[data-theme='light']"), /No rule for :root\[data-theme='light'\]/);
  // A selector that is only a prefix of a longer one, or appears mid-line, does not count as a match.
  assert.throws(() => tokens('.x :root { --text: #000000; }', ':root'), /No rule for :root/);
});

test('ratio() matches the WCAG reference values', () => {
  assert.equal(ratio('#000000', '#ffffff'), 21);
  assert.equal(ratio('#ffffff', '#000000'), 21);
  assert.equal(ratio('#777777', '#777777'), 1);
  // #767676 on white is the well-known 4.54:1 AA threshold grey; #777777 is just under it.
  assert.equal(ratio('#767676', '#ffffff').toFixed(2), '4.54');
  assert.equal(ratio('#777777', '#FFFFFF').toFixed(2), '4.48');
  // Linear segment of the sRGB curve (channel ≤ 0.04045): #0a0a0a → 10/255/12.92.
  const l = 10 / 255 / 12.92;
  assert.ok(Math.abs(ratio('#0a0a0a', '#000000') - (l + 0.05) / 0.05) < 1e-12);
});

test('ratio() rejects anything but #rrggbb', () => {
  for (const bad of ['#fff', 'black', '#gggggg', '000000', undefined]) {
    assert.throws(() => ratio(bad, '#ffffff'), /Not a #rrggbb colour/);
  }
});

test('themes() cascades each theme and palette over its base, as the browser does', () => {
  const t = themes(minimal());
  assert.deepEqual(Object.keys(t), ['dark', 'dark + colour-blind', 'light', 'light + colour-blind']);
  assert.equal(t.dark.text, '#000000');
  // light overrides --text and falls back to :root for everything else.
  assert.equal(t.light.text, '#111111');
  assert.equal(t.light.up, '#000000');
  assert.equal(t.light['surface-1'], '#ffffff');
  // dark + colour-blind: the palette rule over :root.
  assert.equal(t['dark + colour-blind'].up, '#222222');
  assert.equal(t['dark + colour-blind'].text, '#000000');
  // light + colour-blind: the light+palette rule wins, then the palette rule, then light, then :root.
  assert.equal(t['light + colour-blind'].down, '#444444');
  assert.equal(t['light + colour-blind'].up, '#222222');
  assert.equal(t['light + colour-blind'].text, '#111111');
  assert.equal(t['light + colour-blind'].hover, '#ffffff');
});

test('check() returns every foreground on every background in all four combinations', () => {
  const rows = check(minimal());
  assert.equal(rows.length, 4 * fgs.length * bgs.length);
  assert.deepEqual(rows[0], { theme: 'dark', fg: 'text', fgHex: '#000000', bg: 'surface-1', bgHex: '#ffffff', ratio: 21 });
  assert.ok(rows.every((r) => r.ratio > 4.5));
});

test('the shipped styles.scss meets AA 4.5:1 on all 120 pairs', () => {
  const rows = check(shipped);
  assert.equal(rows.length, 120);
  assert.deepEqual(rows.filter((r) => r.ratio < 4.5), []);
});

test('reverting the #214 light-theme tokens fails the check', () => {
  assert.notEqual(reverted214, shipped, 'the #214 tokens are no longer in styles.scss; update this test');
  const failed = check(reverted214).filter((r) => r.ratio < 4.5).map((r) => `${r.theme} ${r.fg} on ${r.bg}`);
  assert.ok(failed.includes('light up on surface-3'), failed.join('\n'));
  assert.ok(failed.includes('light + colour-blind down on surface-3'), failed.join('\n'));
});

test('the CLI checks web/src/styles.scss by default and exits 0 when every pair passes', () => {
  const r = run();
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /^theme +fg +hex +bg +hex +ratio +AA 4\.5:1/);
  assert.match(r.stdout, /120 pairs, 0 below 4\.5:1; worst \d+\.\d\d \(/);
  assert.doesNotMatch(r.stdout, /FAIL/);
});

test('the CLI exits 1 and names the worst pair when one pair is below 4.5:1', () => {
  // #777777 on white is 4.48:1: one failing pair (light + colour-blind down) per background.
  const r = run(file(minimal({ lightColorblind: '--down: #777777;' })));
  assert.equal(r.status, 1, r.stderr);
  assert.match(r.stdout, /light \+ colour-blind  down   #777777  surface-1  #ffffff   4\.48  FAIL/);
  assert.match(r.stdout, /120 pairs, 5 below 4\.5:1; worst 4\.48 \(light \+ colour-blind down on surface-1\)/);
});

test('the CLI exits 1 on the reverted #214 tokens', () => {
  const r = run(file(reverted214));
  assert.equal(r.status, 1, r.stderr);
  assert.match(r.stdout, /light +up +#11804d +surface-3 .* FAIL/);
});

test('the CLI fails loudly on a stylesheet without the theme rules', () => {
  const r = run(file(':root { --text: #000000; }'));
  assert.notEqual(r.status, 0);
  assert.match(r.stderr, /No rule for :root\[data-theme='light'\]/);
});
