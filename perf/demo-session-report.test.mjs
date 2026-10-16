import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import http from 'node:http';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { burstNeeded, partition, peakInFlight, peakInWindow, report, spendsUserToken } from './demo-session-report.mjs';
import { blocks, BLOCK_ROWS } from './cache-memory.mjs';

const call = (start, end, path = '/api/meta/as-of', extra = {}) => ({ pass: 'first', method: 'GET', path, status: 200, cache: 'HIT', bytes: 10, start, end, ...extra });

test('burstNeeded: simultaneous calls need one token each; spaced calls are covered by the refill', () => {
  assert.equal(burstNeeded([], 1), 0);
  assert.equal(burstNeeded([0, 0, 0, 0, 0], 1), 5);
  assert.equal(burstNeeded([0, 1000, 2000, 3000], 1), 1); // one a second at one token a second
  assert.equal(burstNeeded([0, 500, 1000, 1500], 1), 3); // 4 calls in 1.5 s: 4 - 1.5 → 2.5 → 3
  assert.equal(burstNeeded([3000, 0, 1000, 2000], 1), 1); // order doesn't matter
  assert.equal(burstNeeded([0, 1000, 2000, 3000], 0.5), 3); // a slower refill needs more burst (4 - 1.5)
});

test('peakInWindow counts the calls that start within any window', () => {
  assert.equal(peakInWindow([], 10_000), 0);
  assert.equal(peakInWindow([0, 5000, 9999, 10_000, 30_000], 10_000), 3);
  assert.equal(peakInWindow([0, 60_000], 60_000), 1);
});

test('peakInFlight: overlapping calls count together, back-to-back ones do not', () => {
  assert.equal(peakInFlight([]), 0);
  assert.equal(peakInFlight([call(0, 100), call(50, 150), call(60, 70)]), 3);
  assert.equal(peakInFlight([call(0, 100), call(100, 200)]), 1);
  assert.equal(peakInFlight([call(0, 0), call(0, 0)]), 2); // a call that ended at once still held a slot
});

test('the partitions follow RateLimiting.cs: insights reads apart, sign-in outside the user bucket', () => {
  assert.equal(partition(call(0, 1, '/api/insights/core')), 'insights');
  assert.equal(partition(call(0, 1, '/api/positions/query')), 'main');
  assert.equal(spendsUserToken(call(0, 1, '/api/auth/refresh')), false);
  assert.equal(spendsUserToken(call(0, 1, '/api/me')), true);
});

test('report: one row per pass plus the whole session', () => {
  const calls = [
    call(0, 10, '/api/auth/refresh', { cache: null }),
    call(10, 50, '/api/positions/query', { cache: 'MISS', bytes: 2048 }),
    call(20, 30, '/api/insights/core', { cache: 'MISS', bytes: 1024 }),
    call(5000, 5010, '/api/positions/query', { pass: 'repeat' }),
    call(5000, 5001, '/api/deals', { pass: 'repeat', status: 429, cache: null }),
  ];
  const rows = report(calls, 1);
  assert.deepEqual(rows.map((r) => r.pass), ['first', 'repeat', 'session']);
  assert.deepEqual(
    { requests: rows[0].requests, miss: rows[0].miss, missKiB: rows[0].missKiB, burst: rows[0].burstNeeded, main: rows[0].inFlightMain, insights: rows[0].inFlightInsights },
    { requests: 3, miss: 2, missKiB: 3, burst: 2, main: 1, insights: 1 },
  );
  assert.equal(rows[1].refused, 1);
  assert.equal(rows[1].hit, 1);
  assert.equal(rows[2].requests, 5);
  assert.equal(rows[2].peak10s, 4); // the refresh spends no user token
});

test('cache-memory blocks: every block of the book under each sort column, 500 rows apart', () => {
  const all = [...blocks(['a', 'b'], 1200)];
  assert.equal(all.length, 6);
  assert.deepEqual(all.map((b) => [b.sortModel[0].colId, b.startRow, b.endRow]), [
    ['a', 0, 500], ['a', 500, 1000], ['a', 1000, 1500],
    ['b', 0, 500], ['b', 500, 1000], ['b', 1000, 1500],
  ]);
  assert.equal(BLOCK_ROWS, 500);
});

const run = (script, args, env = {}) =>
  spawnSync(process.execPath, [new URL(script, import.meta.url).pathname, ...args], { encoding: 'utf8', env: { PATH: process.env.PATH, ...env } });

test('the report CLI prints the table, and refuses a missing file argument or a bad rate', () => {
  const dir = mkdtempSync(join(tmpdir(), 'demo-session-'));
  const file = join(dir, 'demo-session.json');
  writeFileSync(file, JSON.stringify({ thinkMs: 0, calls: [call(0, 10), call(5, 20, '/api/insights/core', { pass: 'repeat' })] }));
  const ok = run('./demo-session-report.mjs', [file, '--rate', '2']);
  assert.equal(ok.status, 0, ok.stderr);
  assert.match(ok.stdout, /think 0 ms, refill 2\/s/);
  assert.match(ok.stdout, /'session'/);
  assert.equal(run('./demo-session-report.mjs', []).status, 2);
  assert.match(run('./demo-session-report.mjs', [file, '--rate', '0']).stderr, /--rate must be a positive number/);
});

test('cache-memory refuses anything but a local stack, and never runs without credentials', () => {
  const remote = run('./cache-memory.mjs', [], { BASE_URL: 'https://example.onrender.com', DESK_EMAIL: 'a@example.com', DESK_PASSWORD: 'x' });
  assert.equal(remote.status, 1);
  assert.match(remote.stderr, /local stacks only/);
  const anonymous = run('./cache-memory.mjs', [], { BASE_URL: 'http://localhost:9' });
  assert.equal(anonymous.status, 1);
  assert.match(anonymous.stderr, /Set DESK_EMAIL and DESK_PASSWORD/);
  const badSize = run('./cache-memory.mjs', ['--mb', 'lots'], { BASE_URL: 'http://localhost:9', DESK_EMAIL: 'a@example.com', DESK_PASSWORD: 'x' });
  assert.match(badSize.stderr, /--mb must be a positive number/);
});

test('cache-memory fills until the requested size from a local API, counting only stored MISS bodies', async () => {
  const seen = [];
  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', () => {
      seen.push(`${req.method} ${req.url}`);
      res.setHeader('content-type', 'application/json');
      if (req.url === '/api/auth/login') return res.end(JSON.stringify({ accessToken: 't' }));
      if (req.url === '/api/meta/columns') return res.end(JSON.stringify([{ name: 'position_id' }, { name: 'a' }]));
      const q = JSON.parse(body);
      if (q.endRow === 1) return res.end(JSON.stringify({ rowCount: 1500 }));
      res.setHeader('x-cache', q.startRow === 500 ? 'HIT' : 'MISS');
      res.end('x'.repeat(256 * 1024)); // a quarter MB a block
    });
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const { port } = server.address();
  const out = await new Promise((resolve) => {
    const child = spawn(process.execPath, [new URL('./cache-memory.mjs', import.meta.url).pathname, '--mb', '0.5', '--parallel', '1'], {
      env: { PATH: process.env.PATH, BASE_URL: `http://127.0.0.1:${port}`, DESK_EMAIL: 'a@example.com', DESK_PASSWORD: 'x' },
    });
    let stdout = '';
    child.stdout.on('data', (c) => (stdout += c));
    child.on('close', (status) => resolve({ status, stdout }));
  });
  server.close();
  assert.equal(out.status, 0);
  // Blocks 0 (MISS), 500 (HIT, not counted), 1000 (MISS): 0.5 MB of MISS bodies after three requests.
  assert.match(out.stdout, /CACHE_FILL requests=3 miss_mb=0\.5 /);
  assert.match(out.stdout, /"200 HIT":1/);
  assert.equal(seen.filter((s) => s === 'POST /api/positions/query').length, 4);
});

test('cache-memory stops on a refused sign-in', async () => {
  const server = http.createServer((req, res) => { res.statusCode = 401; res.end(); });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const out = await new Promise((resolve) => {
    const child = spawn(process.execPath, [new URL('./cache-memory.mjs', import.meta.url).pathname], {
      env: { PATH: process.env.PATH, BASE_URL: `http://127.0.0.1:${server.address().port}`, DESK_EMAIL: 'a@example.com', DESK_PASSWORD: 'x' },
    });
    let stderr = '';
    child.stderr.on('data', (c) => (stderr += c));
    child.on('close', (status) => resolve({ status, stderr }));
  });
  server.close();
  assert.equal(out.status, 1);
  assert.match(out.stderr, /login failed: HTTP 401/);
});

test('cache-memory defaults BASE_URL to the local stack, and still refuses to run without credentials', () => {
  const out = run('./cache-memory.mjs', []);
  assert.equal(out.status, 1);
  assert.match(out.stderr, /Set DESK_EMAIL and DESK_PASSWORD/);
});

test('cache-memory stops when the book runs out before the size, and counts no refused or unlabelled body', async () => {
  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', () => {
      res.setHeader('content-type', 'application/json');
      if (req.url === '/api/auth/login') return res.end(JSON.stringify({ accessToken: 't' }));
      if (req.url === '/api/meta/columns') return res.end(JSON.stringify([{ name: 'a' }]));
      const q = JSON.parse(body);
      if (q.endRow === 1) return res.end(JSON.stringify({ rowCount: 600 }));
      // Block 0: no X-Cache header at all. Block 500: a refused MISS (503), which stores nothing.
      if (q.startRow === 500) { res.statusCode = 503; res.setHeader('x-cache', 'MISS'); }
      res.end('x'.repeat(1024));
    });
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const out = await new Promise((resolve) => {
    const child = spawn(process.execPath, [new URL('./cache-memory.mjs', import.meta.url).pathname, '--parallel', '2'], {
      env: { PATH: process.env.PATH, BASE_URL: `http://127.0.0.1:${server.address().port}`, DESK_EMAIL: 'a@example.com', DESK_PASSWORD: 'x' },
    });
    let stdout = '';
    child.stdout.on('data', (c) => (stdout += c));
    child.on('close', (status) => resolve({ status, stdout }));
  });
  server.close();
  assert.equal(out.status, 0);
  // Two blocks (600 rows, one sort column), well short of the default 160 MB: both workers drain the queue and stop.
  assert.match(out.stdout, /CACHE_FILL requests=2 miss_mb=0\.0 /);
  assert.match(out.stdout, /"200 -":1/);
  assert.match(out.stdout, /"503 MISS":1/);
});
