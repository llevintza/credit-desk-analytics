#!/usr/bin/env node
// #230: does the time of a failed login tell an unknown email from a real account? Measures, sequentially and
// interleaved, POST /api/auth/login with
//   unknown   a random email that has no account (1 SELECT + the decoy PBKDF2)
//   wrong     DESK_EMAIL with a wrong password (PBKDF2 + the failure-count writes)
//   success   DESK_EMAIL with DESK_PASSWORD (also resets the failure count before lockout; reported, not compared)
// and prints the median, p90 and p99 of each, plus the unknown-vs-wrong median gap.
//
// Usage (local stack only, a throwaway account created with Desk.UserAdmin; raise the login limit for this local
// process only, e.g. RATE_LIMIT_LOGIN_PER_IP_PER_MIN=100000, never on Render):
//   BASE_URL=http://localhost:5180 DESK_EMAIL=… DESK_PASSWORD=… SAMPLES=60 node perf/login-floor.mjs
// The password comes from the environment and is never printed.
import http from 'node:http';
import https from 'node:https';
import { randomUUID } from 'node:crypto';

const base = new URL(process.env.BASE_URL ?? 'http://localhost:8080');
const email = process.env.DESK_EMAIL;
const password = process.env.DESK_PASSWORD;
const samples = Number(process.env.SAMPLES ?? 60);
const warmup = 5;
if (!email || !password) {
  console.error('Set DESK_EMAIL and DESK_PASSWORD (create the account with Desk.UserAdmin).');
  process.exit(2);
}

const agent = base.protocol === 'https:' ? new https.Agent({ keepAlive: true }) : new http.Agent({ keepAlive: true });

/** One login; resolves with the HTTP status and the time to the full response in ms. */
function login(user, pass) {
  const lib = base.protocol === 'https:' ? https : http;
  const body = JSON.stringify({ email: user, password: pass });
  return new Promise((resolve, reject) => {
    const started = process.hrtime.bigint();
    const req = lib.request(new URL('/api/auth/login', base), {
      method: 'POST',
      agent,
      headers: { 'content-type': 'application/json', 'content-length': Buffer.byteLength(body) },
    }, (res) => {
      res.resume();
      res.on('end', () => resolve({ status: res.statusCode, ms: Number(process.hrtime.bigint() - started) / 1e6 }));
    });
    req.on('error', reject);
    req.end(body);
  });
}

async function expect(status, user, pass) {
  const res = await login(user, pass);
  if (res.status !== status) {
    console.error(`expected HTTP ${status}, got ${res.status} (is the login rate limit raised for this local run?)`);
    process.exit(2);
  }
  return res.ms;
}

// Clears any failure count an earlier run left behind.
await expect(200, email, password);
const times = { unknown: [], wrong: [], success: [] };
for (let i = 0; i < warmup + samples; i++) {
  const keep = i >= warmup;
  const unknown = await expect(401, `nobody-${randomUUID()}@example.com`, 'wrong-password-123456');
  const wrong = await expect(401, email, 'wrong-password-123456');
  if (keep) times.unknown.push(unknown);
  if (keep) times.wrong.push(wrong);
  // Lockout is 5 failures: a correct login every 4th round clears the count, so every "wrong" sample is pre-lockout.
  if (i % 4 === 3) {
    const success = await expect(200, email, password);
    if (keep) times.success.push(success);
  }
}

const quantile = (xs, q) => {
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.ceil(q * s.length) - 1)];
};
const fmt = (x) => x.toFixed(1).padStart(7);
console.log(`${base.origin}, ${samples} samples per failed path (after ${warmup} warm-up rounds)`);
console.log('path        n   median      p90      p99      min      max   (ms)');
for (const [name, xs] of Object.entries(times)) {
  console.log(`${name.padEnd(8)} ${String(xs.length).padStart(4)} ${fmt(quantile(xs, 0.5))}  ${fmt(quantile(xs, 0.9))}  ${fmt(quantile(xs, 0.99))}  ${fmt(Math.min(...xs))}  ${fmt(Math.max(...xs))}`);
}
const gap = quantile(times.wrong, 0.5) - quantile(times.unknown, 0.5);
console.log(`median gap, wrong - unknown: ${gap.toFixed(1)} ms`);
agent.destroy();
