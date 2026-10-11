#!/usr/bin/env node
// ADR-0024: how soon a visitor gets the app's static shell (index.html + the initial JS and CSS) from a static host
// (GitHub Pages) versus from the API process (Render), and how long the API process takes to answer at all.
//
// Usage (local stack only; nothing here signs in):
//   node perf/shell-ttfb.mjs --static web/dist/web/browser [samples]   # a gzip static server in this process
//   node perf/shell-ttfb.mjs http://localhost:5180 [samples]            # the API serving the same build
//   node perf/shell-ttfb.mjs --cold http://localhost:5180               # start this, then the API: ms to first 200
//
// Prints p50/p95 of the time to the first byte of index.html and of the whole initial shell (index.html, then
// its <script src> and <link rel=stylesheet> files in parallel), each over a fresh connection.

import { createServer, request } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';
import { gunzipSync, gzipSync } from 'node:zlib';

const args = process.argv.slice(2);
const pct = (xs, p) => {
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.ceil((p / 100) * s.length) - 1)];
};

/** GET over a new connection; resolves with the status, body and ms to the first byte and to the end. */
function get(url) {
  return new Promise((resolve, reject) => {
    const started = performance.now();
    let first = 0;
    const req = request(url, { agent: false, headers: { 'accept-encoding': 'gzip' } }, (res) => {
      const chunks = [];
      res.once('data', () => (first = performance.now() - started));
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve({ status: res.statusCode, body: Buffer.concat(chunks), ttfb: first || performance.now() - started, total: performance.now() - started }));
    });
    req.on('error', reject);
    req.end();
  });
}

const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ico': 'image/x-icon', '.woff2': 'font/woff2' };

/** A static server shaped like GitHub Pages: files as they are, gzip, unknown paths get 404.html. */
async function staticServer(dir) {
  const server = createServer(async (req, res) => {
    const path = normalize(decodeURIComponent(new URL(req.url, 'http://x').pathname)).replace(/^(\.\.[/\\])+/, '');
    let file = join(dir, path.endsWith('/') ? `${path}index.html` : path);
    let body;
    try {
      body = await readFile(file);
    } catch {
      file = join(dir, 'index.html'); // what 404.html (a copy of index.html) serves for a deep link
      body = await readFile(file);
    }
    res.writeHead(200, { 'content-type': types[extname(file)] ?? 'application/octet-stream', 'content-encoding': 'gzip' });
    res.end(gzipSync(body));
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  return { server, base: `http://127.0.0.1:${server.address().port}` };
}

async function shell(base) {
  const index = await get(`${base}/`);
  if (index.status !== 200) throw new Error(`GET / answered ${index.status}`);
  let html = index.body;
  try { html = gunzipSync(html); } catch { /* not gzipped */ }
  const assets = [...html.toString().matchAll(/<(?:script[^>]*src|link[^>]*rel="stylesheet"[^>]*href)="([^"]+)"/g)].map((m) => m[1]);
  const started = performance.now();
  await Promise.all(assets.map((a) => get(new URL(a, `${base}/`).href)));
  return { ttfb: index.ttfb, total: index.total + (performance.now() - started), assets: assets.length };
}

async function series(base, n) {
  for (let i = 0; i < 5; i++) await shell(base); // warm-up: JIT, file cache
  const ttfb = [];
  const total = [];
  let assets = 0;
  for (let i = 0; i < n; i++) {
    const s = await shell(base);
    ttfb.push(s.ttfb);
    total.push(s.total);
    assets = s.assets;
  }
  console.log(`samples: ${n}, initial assets after index.html: ${assets}`);
  console.log('| Static shell | p50 ms | p95 ms |');
  console.log('|---|---:|---:|');
  console.log(`| index.html first byte | ${pct(ttfb, 50).toFixed(2)} | ${pct(ttfb, 95).toFixed(2)} |`);
  console.log(`| index.html + initial JS/CSS | ${pct(total, 50).toFixed(2)} | ${pct(total, 95).toFixed(2)} |`);
}

if (args[0] === '--cold') {
  const started = performance.now();
  for (;;) {
    try {
      const res = await get(new URL('/health', args[1]).href);
      if (res.status === 200) break;
    } catch { /* not listening yet */ }
    await new Promise((r) => setTimeout(r, 25));
  }
  const ready = performance.now() - started;
  const first = await get(`${args[1]}/`);
  console.log(`API process: first 200 from /health after ${ready.toFixed(0)} ms; first index.html after that: ${first.total.toFixed(1)} ms`);
} else if (args[0] === '--static') {
  const { server, base } = await staticServer(args[1]);
  await series(base, Number(args[2] ?? 200));
  server.close();
} else {
  await series(args[0].replace(/\/$/, ''), Number(args[1] ?? 200));
}
