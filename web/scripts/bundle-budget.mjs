#!/usr/bin/env node
// README §10: initial JS < 500 KB compressed — CI fails above it (#76). angular.json budgets measure raw bytes;
// this measures what the browser downloads: Brotli (as the API serves it) of every script and stylesheet the
// built index.html loads up front. Lazy chunks (the AG Grid page) are reported, not budgeted.
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { brotliCompressSync, constants } from 'node:zlib';

const dir = process.argv[2] ?? 'dist/web/browser';
const limitKb = 500;
const html = readFileSync(join(dir, 'index.html'), 'utf8');
const initial = [...html.matchAll(/<(?:script[^>]+src|link[^>]+href)="([^"]+\.(?:js|css))"/g)].map((m) => m[1]);
// Module preloads of the main bundle's static imports are part of the initial load too.
const br = (file) => brotliCompressSync(readFileSync(join(dir, file)), { params: { [constants.BROTLI_PARAM_QUALITY]: 4 } }).length / 1024;

const main = initial.find((f) => f.startsWith('main-'));
const imported = main ? [...readFileSync(join(dir, main), 'utf8').matchAll(/from"\.\/(chunk-[A-Z0-9]+\.js)"/g)].map((m) => m[1]) : [];
const files = [...new Set([...initial, ...imported])];
const total = files.reduce((sum, f) => sum + br(f), 0);
const lazy = readdirSync(dir).filter((f) => f.endsWith('.js') && !files.includes(f));

console.log(`Initial (br): ${total.toFixed(1)} KB of ${limitKb} KB budget — ${files.join(', ')}`);
for (const f of lazy) console.log(`  lazy ${f}: ${br(f).toFixed(1)} KB br`);
if (total > limitKb) {
  console.error(`FAIL: initial bundle ${total.toFixed(1)} KB compressed is over the ${limitKb} KB budget (README §10).`);
  process.exit(1);
}
