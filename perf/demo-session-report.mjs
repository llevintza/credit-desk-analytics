#!/usr/bin/env node
// ADR-0014 (#67): what one demo session asks of the README §7.2 limits and the cache, from the call log that
// e2e/tests/demo-session.spec.ts writes (every /api response the browser saw, refused ones included).
//   node perf/demo-session-report.mjs e2e/test-results/demo-session.json [--rate 1]
// rate: the token bucket's refill per second (RATE_LIMIT_PER_USER_PER_MIN / 60). perf/audit-limits.sql reads the
// same session from app.audit, which holds the admitted requests only.

import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

/** The user's token-bucket partition: everything under /api but sign-in (its own per-IP window and bucket). */
export const spendsUserToken = (call) => !call.path.startsWith('/api/auth/');

/** The per-user concurrency partition a call queues in (RateLimiting.cs): insights source reads, or the main one. */
export const partition = (call) => (call.path.startsWith('/api/insights/') ? 'insights' : 'main');

/**
 * The smallest burst that admits every call at `rate` tokens a second: calls i..j spend j-i+1 tokens while the
 * bucket refills (t_j - t_i) * rate, so the burst must cover the worst such stretch.
 */
export function burstNeeded(starts, rate) {
  const t = [...starts].sort((a, b) => a - b).map((ms) => ms / 1000);
  let worst = 0;
  for (let i = 0; i < t.length; i++) for (let j = i; j < t.length; j++) worst = Math.max(worst, j - i + 1 - (t[j] - t[i]) * rate);
  return Math.max(0, Math.ceil(worst - 1e-9));
}

/** The most calls that start within any `windowMs` (a sliding window opened at each call). */
export function peakInWindow(starts, windowMs) {
  const t = [...starts].sort((a, b) => a - b);
  let peak = 0;
  for (let i = 0, j = 0; i < t.length; i++) {
    while (t[j] < t[i] - windowMs + 1) j++;
    peak = Math.max(peak, i - j + 1);
  }
  return peak;
}

/** The most calls in flight at once ([start, end) intervals): what a concurrency limiter plus its queue must hold. */
export function peakInFlight(calls) {
  const edges = calls.flatMap((c) => [[c.start, 1], [Math.max(c.end, c.start + 1), -1]]).sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  let now = 0;
  let peak = 0;
  for (const [, d] of edges) peak = Math.max(peak, (now += d));
  return peak;
}

export function report(calls, rate = 1) {
  const passes = [...new Set(calls.map((c) => c.pass))];
  const row = (name, mine) => {
    const user = mine.filter(spendsUserToken);
    return {
      pass: name,
      requests: mine.length,
      miss: mine.filter((c) => c.cache === 'MISS').length,
      hit: mine.filter((c) => c.cache === 'HIT').length,
      refused: mine.filter((c) => c.status === 429).length,
      missKiB: Math.round(mine.filter((c) => c.cache === 'MISS').reduce((n, c) => n + c.bytes, 0) / 1024),
      burstNeeded: burstNeeded(user.map((c) => c.start), rate),
      peak10s: peakInWindow(user.map((c) => c.start), 10_000),
      peak60s: peakInWindow(user.map((c) => c.start), 60_000),
      inFlightMain: peakInFlight(user.filter((c) => partition(c) === 'main')),
      inFlightInsights: peakInFlight(user.filter((c) => partition(c) === 'insights')),
    };
  };
  return [...passes.map((p) => row(p, calls.filter((c) => c.pass === p))), row('session', calls)];
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [file, ...rest] = process.argv.slice(2);
  if (!file) {
    console.error('usage: node perf/demo-session-report.mjs <demo-session.json> [--rate <tokens per second>]');
    process.exit(2);
  }
  const at = rest.indexOf('--rate');
  const rate = at >= 0 ? Number(rest[at + 1]) : 1;
  if (!(rate > 0)) {
    console.error('--rate must be a positive number');
    process.exit(2);
  }
  const { thinkMs, calls } = JSON.parse(readFileSync(file, 'utf8'));
  console.log(`think ${thinkMs} ms, refill ${rate}/s`);
  console.table(report(calls, rate));
}
