#!/usr/bin/env node
// #127 / ADR-0005: what a cold MetaCache load costs, and what loading it on one connection at a time changes.
//
// Local stack only (compose or `dotnet run`; never Render, Neon or production), with an admin account created
// locally with Desk.UserAdmin:
//   BASE_URL=http://localhost:5180 DESK_EMAIL=... DESK_PASSWORD=... node perf/meta-cold-load.mjs [loads]
//
// Each round clears the API's caches (POST /api/admin/cache/clear), then times GET /api/meta/as-of, the request
// that reloads the snapshot: the `db` entry of its Server-Timing header is the load itself. Prints p50/p95.
// The password is read from the environment and never printed.

const base = process.env.BASE_URL ?? 'http://localhost:5180';
const email = process.env.DESK_EMAIL;
const password = process.env.DESK_PASSWORD;
const n = Number(process.argv[2] ?? 200);
if (!email || !password) {
  console.error('Set DESK_EMAIL and DESK_PASSWORD (an admin created locally with Desk.UserAdmin).');
  process.exit(2);
}

const pct = (xs, p) => {
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.ceil((p / 100) * s.length) - 1)];
};

const login = await fetch(`${base}/api/auth/login`, {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ email, password }),
});
if (login.status !== 200) {
  console.error(`login failed: HTTP ${login.status}`);
  process.exit(2);
}
const jar = new Map(login.headers.getSetCookie().map((c) => c.split(';')[0].split(/=(.*)/s).slice(0, 2)));
const cookie = [...jar].map(([k, v]) => `${k}=${v}`).join('; ');
const xsrf = decodeURIComponent(jar.get('XSRF-TOKEN') ?? '');

const dbMs = [];
for (let i = 0; i < n; i++) {
  const clear = await fetch(`${base}/api/admin/cache/clear`, { method: 'POST', headers: { cookie, 'x-xsrf-token': xsrf } });
  if (clear.status !== 204) throw new Error(`cache clear: HTTP ${clear.status} (needs an admin account)`);
  const res = await fetch(`${base}/api/meta/as-of`, { headers: { cookie } });
  if (res.status !== 200) throw new Error(`meta: HTTP ${res.status}`);
  const timing = res.headers.get('server-timing') ?? '';
  if (res.headers.get('x-cache') !== 'MISS') throw new Error(`expected a cold load, got X-Cache ${res.headers.get('x-cache')}`);
  dbMs.push(Number(/db;dur=([\d.]+)/.exec(timing)?.[1]));
}
console.log(`cold meta loads: ${n}  db p50 ${pct(dbMs, 50).toFixed(2)} ms  p95 ${pct(dbMs, 95).toFixed(2)} ms  max ${Math.max(...dbMs).toFixed(2)} ms`);
