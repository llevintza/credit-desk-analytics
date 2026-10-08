// node:test for bundle-budget.mjs (#145): every case where the README §10 budget could go unchecked exits 1.
import { randomBytes } from 'node:crypto';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { after, test } from 'node:test';
import assert from 'node:assert/strict';

const script = join(import.meta.dirname, 'bundle-budget.mjs');
const dirs = [];
after(() => dirs.forEach((d) => rmSync(d, { recursive: true, force: true })));

// Random bytes don't compress, so 2 MB stays far over the 500 KB Brotli budget.
const big = () => randomBytes(2 * 1024 * 1024).toString('base64');

function run(files) {
  const dir = mkdtempSync(join(tmpdir(), 'bundle-budget-'));
  dirs.push(dir);
  for (const [name, content] of Object.entries(files)) writeFileSync(join(dir, name), content);
  return spawnSync(process.execPath, [script, dir], { encoding: 'utf8' });
}

const page = (body) => `<!doctype html><html><head></head><body>${body}</body></html>`;

test('passes a small initial bundle and reports lazy chunks', () => {
  const r = run({
    'index.html': page('<link rel="stylesheet" href="styles-A1.css"><script src="main-A1.js" type="module"></script>'),
    'styles-A1.css': 'body{margin:0}',
    'main-A1.js': 'import{a}from"./chunk-SHARED.js";a();',
    'chunk-SHARED.js': 'export const a=()=>1;',
    'chunk-LAZY.js': big(),
  });
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /styles-A1\.css, main-A1\.js, chunk-SHARED\.js/);
  assert.match(r.stdout, /lazy chunk-LAZY\.js/);
});

test('fails an oversize main bundle', () => {
  const r = run({ 'index.html': page('<script src="main-A1.js" type="module"></script>'), 'main-A1.js': big() });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /over the 500 KB budget/);
});

test('fails when index.html references no assets', () => {
  const r = run({ 'index.html': page(''), 'main-A1.js': big() });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /no initial scripts or no main-\*\.js/);
});

test('fails when there is no main-*.js', () => {
  const r = run({ 'index.html': page('<script src="app-A1.js" type="module"></script>'), 'app-A1.js': 'void 0;' });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /no initial scripts or no main-\*\.js/);
});

for (const [label, tag] of [
  ['single-quoted', "<script src='main-A1.js' type='module'></script>"],
  ['unquoted', '<script src=main-A1.js type=module></script>'],
]) {
  test(`counts ${label} attributes`, () => {
    const r = run({ 'index.html': page(tag), 'main-A1.js': big() });
    assert.equal(r.status, 1);
    assert.match(r.stderr, /over the 500 KB budget/);
  });
}

test('counts chunks loaded by side-effect import, transitively', () => {
  const r = run({
    'index.html': page('<script src="main-A1.js" type="module"></script>'),
    'main-A1.js': 'import"./chunk-ONE.js";',
    'chunk-ONE.js': 'import "./chunk-TWO.js";',
    'chunk-TWO.js': big(),
  });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /over the 500 KB budget/);
});

test('fails when a counted file is 0 bytes', () => {
  const r = run({ 'index.html': page('<script src="main-A1.js" type="module"></script>'), 'main-A1.js': '' });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /0 bytes/);
});

test('fails when a referenced chunk is missing', () => {
  const r = run({ 'index.html': page('<script src="main-A1.js" type="module"></script>'), 'main-A1.js': 'import"./chunk-GONE.js";' });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /missing/);
});
