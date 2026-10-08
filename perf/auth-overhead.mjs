#!/usr/bin/env node
// ADR-0005: what the cookie session costs per request (README §7.1).
//
// Usage (API running, an account created with Desk.UserAdmin; raise the per-user limit for the run):
//   RATE_LIMIT_PER_USER_PER_MIN=100000 RATE_LIMIT_PER_USER_BURST=100000 dotnet run -c Release --project src/Desk.Api
//   BASE_URL=http://localhost:5180 DESK_EMAIL=... DESK_PASSWORD=... node perf/auth-overhead.mjs [requests]
//
// Prints login latency, session-cookie size, and p50/p95 of GET /health (no auth), anonymous GET /api/me (401)
// and authenticated GET /api/me (cookie decrypt + claims, no database). The password is read from the
// environment and never printed.

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
let cookie = '';
for (let i = 0; i < 3; i++) {
  let res;
  logins.push(await time(async () => {
    res = await fetch(`${base}/api/auth/login`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });
    await res.arrayBuffer();
  }));
  if (res.status !== 200) throw new Error(`login failed: ${res.status}`);
  cookie = res.headers.getSetCookie().find((c) => c.startsWith('__Host-desk='))?.split(';')[0] ?? '';
}

console.log(`requests per series: ${n}`);
console.log(`login ms (3 runs): ${logins.map((x) => x.toFixed(1)).join(', ')}`);
console.log(`session cookie: ${cookie.length} bytes ("__Host-desk=<value>")`);
console.log('');
console.log('| Request                            |  p50 ms |  p95 ms |');
console.log('|------------------------------------|--------:|--------:|');
await series({ name: 'GET /health (no auth)', path: '/health' }, {}, 200);
await series({ name: 'GET /api/me anonymous (401)', path: '/api/me' }, {}, 401);
await series({ name: 'GET /api/me with session cookie', path: '/api/me' }, { headers: { cookie } }, 200);
