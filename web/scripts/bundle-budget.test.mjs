// node:test for bundle-budget.mjs (#145): every case where the README §10 budget could go unchecked exits 1.
import { randomBytes } from 'node:crypto';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { brotliCompressSync, constants } from 'node:zlib';
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
  for (const [name, content] of Object.entries(files)) {
    mkdirSync(dirname(join(dir, name)), { recursive: true });
    writeFileSync(join(dir, name), content);
  }
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

// #236: README §10 says "< 500 KB", so exactly 500 KB (512000 bytes) fails and one byte under passes. Random bytes
// barely compress, so trimming the input by the measured overshoot lands the q1 output on the exact byte count.
const brotliQ1 = (buffer) => brotliCompressSync(buffer, { params: { [constants.BROTLI_PARAM_QUALITY]: 1 } }).length;
const compressesTo = (bytes) => {
  const pool = randomBytes(bytes + 1024);
  for (let n = bytes, i = 0; i < 20; i++) {
    const over = brotliQ1(pool.subarray(0, n)) - bytes;
    if (over === 0) return pool.subarray(0, n);
    n -= over;
  }
  assert.fail(`no fixture compresses to exactly ${bytes} bytes at q1`);
};
for (const [label, bytes, status] of [
  ['fails at exactly 500 KB', 500 * 1024, 1],
  ['passes one byte under 500 KB', 500 * 1024 - 1, 0],
]) {
  test(`${label} compressed`, () => {
    const r = run({ 'index.html': page('<script src="main-A1.js" type="module"></script>'), 'main-A1.js': compressesTo(bytes) });
    assert.equal(r.status, status, r.stdout + r.stderr);
    if (status === 1) assert.match(r.stderr, /500\.0 KB compressed is at or over the 500 KB budget/);
  });
}

// #203: the API serves Brotli at CompressionLevel.Fastest (quality 1). A 400 KB random block repeated once is
// ~600 KB at q1, which misses the distant repeat, but ~300 KB at q4, which finds it: only measuring at q1 fails.
test('measures at the served Brotli quality (q1), not q4', () => {
  const block = randomBytes(300 * 1024).toString('base64');
  const r = run({ 'index.html': page('<script src="main-A1.js" type="module"></script>'), 'main-A1.js': block + block });
  assert.equal(r.status, 1, r.stdout);
  assert.match(r.stdout, /Initial \(br q1\)/);
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

test('refuses paths outside the dist dir', () => {
  const r = run({ 'index.html': page('<script src="../outside.js"></script><script src="main-A1.js" type="module"></script>'), 'main-A1.js': 'void 0;' });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /resolves outside/);
});

test('ignores non-js/css links such as manifest.json', () => {
  const r = run({
    'index.html': page('<link rel="manifest" href="manifest.json"><script src="main-A1.js" type="module"></script>'),
    'manifest.json': '{}',
    'main-A1.js': 'void 0;',
  });
  assert.equal(r.status, 0, r.stderr);
});

test('terminates on an import cycle and counts each chunk once', () => {
  const r = run({
    'index.html': page('<link rel="modulepreload" href="chunk-ONE.js"><script src="main-A1.js" type="module"></script>'),
    'main-A1.js': 'import"./chunk-ONE.js";',
    'chunk-ONE.js': 'import"./chunk-TWO.js";import"./main-A1.js";',
    'chunk-TWO.js': 'import"./chunk-ONE.js";',
  });
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /chunk-ONE\.js, main-A1\.js, chunk-TWO\.js$/m);
});

test('fails when index.html is missing', () => {
  const r = run({ 'main-A1.js': 'void 0;' });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /index\.html is missing/);
});

// #204 F4: every relative static import is followed, resolved against its importer, whatever the file is called.
for (const [label, files] of [
  ['a non-chunk name', { 'main-A1.js': 'import{v}from"./vendor-A1.js";', 'vendor-A1.js': big() }],
  ['a dotted name', { 'main-A1.js': 'import"./chunk.part-A1.js";', 'chunk.part-A1.js': big() }],
  ['a sub-path', { 'main-A1.js': 'export*from"./lib/deep/x-A1.js";', 'lib/deep/x-A1.js': big() }],
  ['a ../ path relative to its importer', { 'main-A1.js': 'import"./lib/a.js";', 'lib/a.js': 'import"../b.js";', 'b.js': big() }],
  ['a site-rooted path', { 'main-A1.js': 'import"/lib/x.js";', 'lib/x.js': big() }],
]) {
  test(`counts a static import of ${label}`, () => {
    const r = run({ 'index.html': page('<script src="main-A1.js" type="module"></script>'), ...files });
    assert.equal(r.status, 1, r.stdout);
    assert.match(r.stderr, /over the 500 KB budget/);
  });
}

test('leaves dynamic import() lazy and lists lazy files in sub-paths', () => {
  const r = run({
    'index.html': page('<script src="main-A1.js" type="module"></script>'),
    'main-A1.js': 'const p=()=>import("./lazy/page-A1.js");const q=()=>import ("./lazy/page-A1.js");',
    'lazy/page-A1.js': big(),
  });
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /lazy lazy\/page-A1\.js/);
});

test('refuses a static import that resolves outside the dist dir', () => {
  const r = run({ 'index.html': page('<script src="main-A1.js" type="module"></script>'), 'main-A1.js': 'import"../../outside.js";' });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /resolves outside/);
});

test('counts a file once however its import is spelled', () => {
  const r = run({
    'index.html': page('<script src="main-A1.js" type="module"></script>'),
    'main-A1.js': 'import"./lib/a.js";import"./lib/../lib/a.js";import"/lib/a.js?v=1";',
    'lib/a.js': 'void 0;',
  });
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /budget — main-A1\.js, lib\/a\.js$/m);
});

// #204 F5: index.html tags are parsed case-insensitively, in any attribute order and spacing, and the src is the
// script's own src (not the last `src=` in the tag, such as data-src).
const bigMain = () => ({ 'main-A1.js': big() });
for (const [label, tag, files] of [
  ['upper-case tags', '<SCRIPT TYPE="module" SRC="main-A1.js"></SCRIPT>', bigMain],
  ['whitespace around =', '<script type = "module" src = "main-A1.js"></script>', bigMain],
  ['a later data-src', '<script src="main-A1.js" data-src="tiny.js" type="module"></script>', () => ({ ...bigMain(), 'tiny.js': 'void 0;' })],
  ['a quoted > before src', '<script data-note="a>b" src="main-A1.js" type="module"></script>', bigMain],
  [
    'an upper-case modulepreload link',
    "<LINK REL=modulepreload HREF = 'chunk-A1.js'><script src=\"main-A1.js\" type=\"module\"></script>",
    () => ({ 'main-A1.js': 'void 0;', 'chunk-A1.js': big() }),
  ],
]) {
  test(`counts scripts written with ${label}`, () => {
    const r = run({ 'index.html': page(tag), ...files() });
    assert.equal(r.status, 1, r.stdout);
    assert.match(r.stderr, /over the 500 KB budget/);
  });
}

test('skips nomodule scripts, which module browsers never download', () => {
  const r = run({
    'index.html': page('<script nomodule src="legacy-A1.js"></script><script src="main-A1.js" type="module"></script>'),
    'legacy-A1.js': big(),
    'main-A1.js': 'void 0;',
  });
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /budget — main-A1\.js$/m);
});

test('ignores tags inside HTML comments', () => {
  const r = run({ 'index.html': page('<!-- <script src="gone.js"></script> --><script src="main-A1.js" type="module"></script>'), 'main-A1.js': 'void 0;' });
  assert.equal(r.status, 0, r.stderr);
});

test('counts chunks a static import in an inline module script loads', () => {
  const r = run({
    'index.html': page('<script src="main-A1.js" type="module"></script><script type="module">import "./boot-A1.js";</script>'),
    'main-A1.js': 'void 0;',
    'boot-A1.js': big(),
  });
  assert.equal(r.status, 1, r.stdout);
  assert.match(r.stderr, /over the 500 KB budget/);
});

for (const [label, tag, message] of [
  ['an unterminated tag', '<script src="main-A1.js" type="module"></script><script src="extra-A1.js', /cannot be parsed/],
  ['a script that is not .js', '<script src="main-A1.js" type="module"></script><script src="extra-A1.mjs" type="module"></script>', /not a \.js file/],
  ['a stylesheet that is not .css', '<link rel="stylesheet" href="theme-A1.php"><script src="main-A1.js" type="module"></script>', /not a \.js or \.css file/],
]) {
  test(`fails closed on ${label}`, () => {
    const r = run({ 'index.html': page(tag), 'main-A1.js': 'void 0;', 'extra-A1.js': big(), 'extra-A1.mjs': big(), 'theme-A1.php': big() });
    assert.equal(r.status, 1, r.stdout);
    assert.match(r.stderr, message);
  });
}

// #266 N1: `<!--` is only a comment outside <script>/<style> bodies and quoted attributes. Stripping it there would
// swallow every tag up to the next `-->`.
for (const [label, decoy] of [
  ['a script body', '<script>var s="<!--";</script>'],
  ['a style body', '<style>p::before{content:"<!--"}</style>'],
  ['a quoted attribute', '<div title="<!--"></div>'],
]) {
  test(`counts scripts after a <!-- in ${label}`, () => {
    const r = run({
      'index.html': page(`<script src="main-A1.js" type="module"></script>${decoy}<script src="chunk-BIG.js"></script><!-- -->`),
      'main-A1.js': 'void 0;',
      'chunk-BIG.js': big(),
    });
    assert.equal(r.status, 1, r.stdout);
    assert.match(r.stderr, /over the 500 KB budget/);
  });
}
