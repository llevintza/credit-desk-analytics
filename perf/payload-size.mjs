#!/usr/bin/env node
// README §10 payload budgets for the P1 first block, measured on the wire (compressed bytes as sent):
//   Risk preset (≈40 columns): ≤ 60 KB  → exit 1 above budget (the CI budgets job fails)
//   All preset (≈200 columns): ≤ 250 KB → warning only
// and the largest P2 response (README §6: fund 1 ITD, 69 months): < 5 KB → exit 1 above budget.
//
// Usage (a running, seeded API; an account from Desk.UserAdmin):
//   BASE_URL=http://localhost:8080 DESK_EMAIL=… DESK_PASSWORD=… node perf/payload-size.mjs
// The password comes from the environment and is never printed.
import http from 'node:http';
import https from 'node:https';

const base = new URL(process.env.BASE_URL ?? 'http://localhost:8080');
const email = process.env.DESK_EMAIL;
const password = process.env.DESK_PASSWORD;
if (!email || !password) {
  console.error('Set DESK_EMAIL and DESK_PASSWORD (create the account with Desk.UserAdmin).');
  process.exit(2);
}

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

const cookies = new Map();
const keep = (res) => {
  for (const c of res.headers['set-cookie'] ?? []) {
    const [pair] = c.split(';');
    const i = pair.indexOf('=');
    cookies.set(pair.slice(0, i), pair.slice(i + 1));
  }
};
const cookieHeader = () => [...cookies].map(([k, v]) => `${k}=${v}`).join('; ');

const login = await request('POST', '/api/auth/login', {
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ email, password }),
});
if (login.status !== 200) {
  console.error(`login failed: HTTP ${login.status}`);
  process.exit(2);
}
keep(login);

const presets = JSON.parse((await request('GET', '/api/presets/positions', { headers: { cookie: cookieHeader() } })).body);
let failed = false;
console.log('| Preset | Columns | Encoding | Wire KB | Budget KB | Result |');
console.log('|---|---:|---|---:|---:|---|');
for (const { preset, limitKb, failOver } of budgets) {
  const columns = presets.find((p) => p.name === preset)?.state?.columns;
  if (!columns) throw new Error(`built-in preset ${preset} not found`);
  const res = await request('POST', '/api/positions/query', {
    headers: {
      'content-type': 'application/json',
      'accept-encoding': 'br, gzip',
      'x-xsrf-token': decodeURIComponent(cookies.get('XSRF-TOKEN') ?? ''),
      cookie: cookieHeader(),
    },
    body: JSON.stringify({ startRow: 0, endRow: 200, columns }),
  });
  if (res.status !== 200) throw new Error(`${preset}: HTTP ${res.status} ${res.body}`);
  const kb = res.body.length / 1024;
  const over = kb > limitKb;
  if (over && failOver) failed = true;
  const result = over ? (failOver ? 'FAIL' : 'WARN') : 'ok';
  console.log(`| ${preset} | ${columns.length + 1} | ${res.headers['content-encoding'] ?? 'identity'} | ${kb.toFixed(1)} | ${limitKb} | ${result} |`);
  if (over && !failOver) console.log(`::warning::${preset} first block is ${kb.toFixed(1)} KB, over the ${limitKb} KB budget`);
}

const fund = await request('GET', '/api/funds/1/performance?range=ITD', { headers: { 'accept-encoding': 'br, gzip', cookie: cookieHeader() } });
if (fund.status !== 200) throw new Error(`P2 fund 1 ITD: HTTP ${fund.status} ${fund.body}`);
const fundKb = fund.body.length / 1024;
const fundOver = fundKb >= 5;
if (fundOver) failed = true;
console.log(`| P2 fund 1 ITD | – | ${fund.headers['content-encoding'] ?? 'identity'} | ${fundKb.toFixed(1)} | 5 | ${fundOver ? 'FAIL' : 'ok'} |`);
process.exit(failed ? 1 : 0);
