#!/usr/bin/env node
// README §10 payload budgets for the P1 first block, measured on the wire (compressed bytes as sent):
//   Risk preset (≈40 columns): ≤ 60 KB  → exit 1 above budget (the CI budgets job fails)
//   All preset (≈200 columns): ≤ 250 KB → warning only
// and the largest P2 response (README §6: fund 1 ITD, 69 months): < 5 KB → exit 1 above budget.
//
// Usage (a running, seeded API; an account from Desk.UserAdmin):
//   BASE_URL=http://localhost:8080 DESK_EMAIL=… DESK_PASSWORD=… node perf/payload-size.mjs
// The password comes from the environment and is never printed.
//
// Fails closed (#136): exit 2 when any call is not a 200, a body doesn't decode or parse, or a P1 block
// isn't the requested shape (columns n+1, 200 rows in every column, rowCount ≥ 200). Exit 1 is over budget only.
import http from 'node:http';
import https from 'node:https';
import { pathToFileURL } from 'node:url';
import { brotliDecompressSync, gunzipSync } from 'node:zlib';

const BLOCK_ROWS = 200;

/** The body as sent, decompressed by its content-encoding (the byte count stays the wire one). */
export function decodeBody({ headers, body }) {
  const enc = headers['content-encoding'] ?? 'identity';
  if (enc === 'br') return brotliDecompressSync(body);
  if (enc === 'gzip') return gunzipSync(body);
  if (enc === 'identity') return body;
  throw new Error(`unsupported content-encoding ${enc}`);
}

/** Why a P1 first block isn't the requested shape, or null when it is: id + every requested column, 200 rows each. */
export function blockProblem(block, columns, rows = BLOCK_ROWS) {
  if (block === null || typeof block !== 'object' || Array.isArray(block)) return 'body is not an object';
  const want = columns.length + 1;
  if (!Array.isArray(block.columns)) return 'no columns array';
  if (block.columns.length !== want) return `${block.columns.length} columns, expected ${want}`;
  if (!Array.isArray(block.data)) return 'data is not an array';
  if (block.data.length !== want) return `${block.data.length} data columns, expected ${want}`;
  for (let i = 0; i < want; i++) {
    const n = Array.isArray(block.data[i]) ? block.data[i].length : 0;
    if (n !== rows) return i === 0 ? `${n} rows in data[0], expected ${rows}` : `data[${i}] has ${n} rows, expected ${rows}`;
  }
  if (typeof block.rowCount !== 'number' || block.rowCount < rows) return `rowCount ${JSON.stringify(block.rowCount)}, expected ≥ ${rows}`;
  return null;
}

/** Thrown to stop with exit 2 (a broken stack or response); main() turns it into its return code. */
class Broken extends Error {}
function fail(message) {
  throw new Broken(message);
}

/** Decoded JSON, or exit 2. */
function json(label, res) {
  try {
    return JSON.parse(decodeBody(res).toString('utf8'));
  } catch (e) {
    return fail(`${label}: body is not JSON (${e.message})`);
  }
}

/** Runs the checks and returns the exit code: 0 within budget, 1 over a failing budget, 2 broken. */
export async function main(env = process.env, log = console.log, error = console.error) {
  try {
    return await run(env, log);
  } catch (e) {
    if (!(e instanceof Broken)) throw e;
    error(e.message);
    return 2;
  }
}

async function run(env, log) {
  const base = new URL(env.BASE_URL ?? 'http://localhost:8080');
  const email = env.DESK_EMAIL;
  const password = env.DESK_PASSWORD;
  if (!email || !password) fail('Set DESK_EMAIL and DESK_PASSWORD (create the account with Desk.UserAdmin).');

  const budgets = [
    { preset: 'Risk', limitKb: 60, failOver: true },
    { preset: 'All', limitKb: 250, failOver: false },
  ];

  /** Raw request: no automatic decompression, so the byte count is what crossed the wire. */
  function request(method, path, { headers = {}, body } = {}) {
    const lib = base.protocol === 'https:' ? https : http;
    return new Promise((resolve, reject) => {
      const req = lib.request(new URL(path, base), { method, headers }, (res) => {
        const chunks = [];
        res.on('data', (c) => chunks.push(c));
        res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks) }));
      });
      req.on('error', reject);
      if (body) req.write(body);
      req.end();
    });
  }

  const login = await request('POST', '/api/auth/login', {
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });
  if (login.status !== 200) fail(`login failed: HTTP ${login.status}`);
  // Bearer token (ADR-0024); the run is far shorter than the token's 5 minutes.
  const authorization = `Bearer ${json('login', login).accessToken}`;

  const pr = await request('GET', '/api/presets/positions', { headers: { authorization } });
  if (pr.status !== 200) fail(`presets: HTTP ${pr.status}`);
  const presets = json('presets', pr);
  if (!Array.isArray(presets)) fail('presets: body is not an array');
  let failed = false;
  log('| Preset | Columns | Rows | Encoding | Wire KB | Budget KB | Result |');
  log('|---|---:|---:|---|---:|---:|---|');
  for (const { preset, limitKb, failOver } of budgets) {
    const columns = presets.find((p) => p.name === preset)?.state?.columns;
    if (!Array.isArray(columns) || columns.length === 0) fail(`built-in preset ${preset} not found`);
    const res = await request('POST', '/api/positions/query', {
      headers: {
        'content-type': 'application/json',
        'accept-encoding': 'br, gzip',
        authorization,
      },
      body: JSON.stringify({ startRow: 0, endRow: BLOCK_ROWS, columns }),
    });
    if (res.status !== 200) fail(`${preset}: HTTP ${res.status} ${res.body}`);
    const block = json(preset, res);
    const problem = blockProblem(block, columns);
    if (problem) fail(`${preset}: unexpected block (${problem})`);
    const kb = res.body.length / 1024;
    const over = kb > limitKb;
    if (over && failOver) failed = true;
    const result = over ? (failOver ? 'FAIL' : 'WARN') : 'ok';
    log(`| ${preset} | ${block.columns.length} | ${block.data[0].length} | ${res.headers['content-encoding'] ?? 'identity'} | ${kb.toFixed(1)} | ${limitKb} | ${result} |`);
    if (over && !failOver) log(`::warning::${preset} first block is ${kb.toFixed(1)} KB, over the ${limitKb} KB budget`);
  }

  const fund = await request('GET', '/api/funds/1/performance?range=ITD', { headers: { 'accept-encoding': 'br, gzip', authorization } });
  if (fund.status !== 200) fail(`P2 fund 1 ITD: HTTP ${fund.status} ${fund.body}`);
  const fundBody = json('P2 fund 1 ITD', fund);
  if (fundBody === null || typeof fundBody !== 'object') fail('P2 fund 1 ITD: body is not an object');
  const fundKb = fund.body.length / 1024;
  const fundOver = fundKb >= 5;
  if (fundOver) failed = true;
  log(`| P2 fund 1 ITD | – | – | ${fund.headers['content-encoding'] ?? 'identity'} | ${fundKb.toFixed(1)} | 5 | ${fundOver ? 'FAIL' : 'ok'} |`);

  // P3 has no payload budget (README §10): each source's response is reported for the ADR-0012 numbers.
  for (const source of ['core', 'market', 'surveillance', 'pricing', 'reference']) {
    const res = await request('GET', `/api/insights/${source}`, { headers: { 'accept-encoding': 'br, gzip', authorization } });
    if (res.status !== 200) fail(`P3 ${source}: HTTP ${res.status} ${res.body}`);
    json(`P3 ${source}`, res);
    log(`| P3 insights ${source} | – | – | ${res.headers['content-encoding'] ?? 'identity'} | ${(res.body.length / 1024).toFixed(1)} | – | report |`);
  }
  return failed ? 1 : 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) process.exit(await main());
