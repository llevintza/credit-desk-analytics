#!/usr/bin/env node
// #252: checks web/scripts/bundle-budget.mjs against the bytes the API really sends. For every .js and .css file at
// the root of the built SPA, it GETs the file from a running API with `Accept-Encoding: br` and counts the raw body
// bytes (node:http does not decompress), then prints them next to the script's measure (q1 in 16 KB slices) and the
// old one-shot q1 measure. Local stack only: BASE_URL=http://localhost:8080 node perf/bundle-wire-bytes.mjs web/dist/web/browser
import { readdirSync, readFileSync } from 'node:fs';
import { get } from 'node:http';
import { join } from 'node:path';
import { brotliCompressSync, constants } from 'node:zlib';

const base = process.env.BASE_URL ?? 'http://localhost:8080';
const dir = process.argv[2] ?? 'web/dist/web/browser';
const q1 = (buffer) => brotliCompressSync(buffer, { params: { [constants.BROTLI_PARAM_QUALITY]: 1 } }).length;
const sliced = (buffer) => {
  let size = 0;
  for (let at = 0; at < buffer.length; at += 16 * 1024) size += q1(buffer.subarray(at, at + 16 * 1024));
  return size;
};
const wire = (path) =>
  new Promise((resolve, reject) =>
    get(new URL(path, base), { headers: { 'accept-encoding': 'br' } }, (res) => {
      let size = 0;
      res.on('data', (chunk) => (size += chunk.length));
      res.on('end', () =>
        res.statusCode === 200 && res.headers['content-encoding'] === 'br'
          ? resolve(size)
          : reject(new Error(`${path}: HTTP ${res.statusCode}, content-encoding ${res.headers['content-encoding']}`)),
      );
    }).on('error', reject),
  );

const kb = (bytes) => (bytes / 1024).toFixed(1);
let failed = false;
console.log('| File | Raw KB | Wire br KB | Script (q1, 16 KB slices) KB | Script − wire (bytes) | One-shot q1 KB |');
console.log('|---|---|---|---|---|---|');
for (const file of readdirSync(dir).filter((f) => /\.(?:js|css)$/.test(f)).sort()) {
  const bytes = readFileSync(join(dir, file));
  const [onWire, measured] = [await wire(`/${file}`), sliced(bytes)];
  if (measured < onWire) failed = true;
  console.log(`| ${file} | ${kb(bytes.length)} | ${kb(onWire)} | ${kb(measured)} | ${measured - onWire}${measured < onWire ? ' (UNDER)' : ''} | ${kb(q1(bytes))} |`);
}
if (failed) {
  console.error('FAIL: the 16 KB-slice measure is under the wire bytes for a file.');
  process.exit(1);
}
