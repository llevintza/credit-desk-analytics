#!/usr/bin/env node
// ADR-0024 (was ADR-0005's cookie measurement): what the bearer token costs per request (README §7.1).
//
// Usage (local stack only, an account created with Desk.UserAdmin; raise the per-user limit for the run and allow
// the preflight origin):
//   RATE_LIMIT_PER_USER_PER_MIN=100000 RATE_LIMIT_PER_USER_BURST=100000 CORS_ALLOWED_ORIGINS=https://pages.example.test \
//     dotnet run -c Release --project src/Desk.Api
//   BASE_URL=http://localhost:5180 DESK_EMAIL=... DESK_PASSWORD=... node perf/auth-overhead.mjs [requests]
//
// Prints login and refresh latency, the Authorization header size, and p50/p95 of GET /health (no auth), anonymous
// GET /api/me (401), authenticated GET /api/me (token unprotect + claims, no database) and a CORS preflight. The
// password and the tokens are never printed.

const base = process.env.BASE_URL ?? 'http://localhost:5180';
const email = process.env.DESK_EMAIL;
const password = process.env.DESK_PASSWORD;
const n = Number(process.argv[2] ?? 500);
if (!email || !password) {
  console.error('Set DESK_EMAIL and DESK_PASSWORD (create the account with Desk.UserAdmin).');
  process.exit(1);
}

const pct = (xs, p) => {
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.ceil((p / 100) * s.length) - 1)];
};
const fmt = (x) => x.toFixed(2).padStart(7);

async function time(fn) {
  const t = performance.now();
  await fn();
  return performance.now() - t;
}

async function series(label, init, expect) {
  const ms = [];
  for (let i = 0; i < 20; i++) await fetch(`${base}${label.path}`, init); // warm-up
  for (let i = 0; i < n; i++) {
    ms.push(await time(async () => {
      const res = await fetch(`${base}${label.path}`, init);
      await res.arrayBuffer();
      if (res.status !== expect) throw new Error(`${label.name}: expected ${expect}, got ${res.status}`);
    }));
  }
  console.log(`| ${label.name.padEnd(34)} | ${fmt(pct(ms, 50))} | ${fmt(pct(ms, 95))} |`);
}

// Login: PBKDF2 verification dominates. Three runs stay under the 5/min per-IP login limit.
const logins = [];
let tokens;
for (let i = 0; i < 3; i++) {
  let res;
  logins.push(await time(async () => {
    res = await fetch(`${base}/api/auth/login`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });
    tokens = await res.json().catch(() => null);
  }));
  if (res.status !== 200) throw new Error(`login failed: ${res.status}`);
}

// Refresh: unprotect, a security-stamp and account read, and a new pair. Each one renews the refresh token.
const refreshes = [];
for (let i = 0; i < 20; i++) {
  let res;
  refreshes.push(await time(async () => {
    res = await fetch(`${base}/api/auth/refresh`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ refreshToken: tokens.refreshToken }),
    });
    tokens = await res.json().catch(() => null);
  }));
  if (res.status !== 200) throw new Error(`refresh failed: ${res.status}`);
}
const authorization = `Bearer ${tokens.accessToken}`;

console.log(`requests per series: ${n}`);
console.log(`login ms (3 runs): ${logins.map((x) => x.toFixed(1)).join(', ')}`);
console.log(`refresh ms (20 runs): p50 ${pct(refreshes, 50).toFixed(1)}, p95 ${pct(refreshes, 95).toFixed(1)}`);
console.log(`Authorization header: ${'Authorization: '.length + authorization.length} bytes; refresh token: ${tokens.refreshToken.length} bytes`);
console.log('');
console.log('| Request                            |  p50 ms |  p95 ms |');
console.log('|------------------------------------|--------:|--------:|');
await series({ name: 'GET /health (no auth)', path: '/health' }, {}, 200);
await series({ name: 'GET /api/me anonymous (401)', path: '/api/me' }, {}, 401);
await series({ name: 'GET /api/me with bearer token', path: '/api/me' }, { headers: { authorization } }, 200);
await series({ name: 'OPTIONS preflight (CORS)', path: '/api/positions/query' }, {
  method: 'OPTIONS',
  headers: {
    origin: process.env.PREFLIGHT_ORIGIN ?? 'https://pages.example.test',
    'access-control-request-method': 'POST',
    'access-control-request-headers': 'authorization,content-type',
  },
}, 204);
