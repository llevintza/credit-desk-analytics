#!/usr/bin/env node
// README §10: initial JS < 500 KB compressed — CI fails above it (#76). angular.json budgets measure raw bytes;
// this measures what the browser downloads: Brotli (as the API serves it) of every script and stylesheet the
// built index.html loads up front. Lazy chunks (the AG Grid page) are reported, not budgeted.
// Fails closed (#145): anything that would leave the budget unchecked exits 1 instead of passing.
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join, resolve, sep } from 'node:path';
import { brotliCompressSync, constants } from 'node:zlib';

const dir = process.argv[2] ?? 'dist/web/browser';
const limitKb = 500;
const fail = (message) => {
  console.error(`FAIL: ${message}`);
  process.exit(1);
};

// index.html paths may not leave the dist dir (`../…`); only files the build emitted are measured.
const root = resolve(dir);
const read = (file) => {
  const path = resolve(root, file);
  if (!path.startsWith(root + sep)) fail(`${file} resolves outside ${root}; refusing to read it.`);
  if (!existsSync(path)) fail(`${path} is referenced by the initial load but missing.`);
  return readFileSync(path);
};

const html = readFileSync(join(dir, 'index.html'), 'utf8');
// Double-quoted, single-quoted and unquoted attributes all count. The extension must end the path (a query or
// fragment may follow), so `manifest.json` is not read as `manifest.js`.
const initial = [...html.matchAll(/<(?:script[^>]+src|link[^>]+href)=["']?([^"'\s>?#]+\.(?:js|css))(?=[?#"'\s>])/g)].map((m) => m[1]);
const main = initial.find((f) => /^main-[^/]*\.js$/.test(f));
if (initial.length === 0 || !main) fail('index.html lists no initial scripts or no main-*.js; refusing to pass the budget.');

// Static imports of the initial scripts load up front too: both `from"./chunk-…"` and the side-effect
// `import"./chunk-…"` esbuild emits, followed transitively.
const files = [...new Set(initial)];
const queue = files.filter((f) => f.endsWith('.js'));
while (queue.length > 0) {
  for (const [, chunk] of read(queue.shift()).toString('utf8').matchAll(/(?:from|import)\s*["']\.\/(chunk-[A-Za-z0-9_-]+\.js)["']/g)) {
    if (!files.includes(chunk)) {
      files.push(chunk);
      queue.push(chunk);
    }
  }
}

for (const f of files) if (read(f).length === 0) fail(`${f} is 0 bytes; refusing to pass the budget.`);

const br = (file) => brotliCompressSync(read(file), { params: { [constants.BROTLI_PARAM_QUALITY]: 4 } }).length / 1024;
const total = files.reduce((sum, f) => sum + br(f), 0);
const lazy = readdirSync(dir).filter((f) => f.endsWith('.js') && !files.includes(f));

console.log(`Initial (br): ${total.toFixed(1)} KB of ${limitKb} KB budget — ${files.join(', ')}`);
for (const f of lazy) console.log(`  lazy ${f}: ${br(f).toFixed(1)} KB br`);
if (total > limitKb) fail(`initial bundle ${total.toFixed(1)} KB compressed is over the ${limitKb} KB budget (README §10).`);
