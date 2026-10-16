#!/usr/bin/env node
// ADR-0015: the P6 mark stream measured end to end through the real pipeline (bearer auth, rate limiter, audit,
// entitlements), the way the SPA reads it: fetch with an Authorization header, not EventSource.
//
// Usage (local stack only, an account created with Desk.UserAdmin; never Render, Neon or production):
//   BASE_URL=http://localhost:8080 DESK_EMAIL=... DESK_PASSWORD=... node perf/marks-stream.mjs [seconds=60]
//
// Reports: the access token's size (what every polled request would carry), time to the hello event, body bytes
// and marks per minute, mark latency (receipt - event_ts, live marks only), how many marks arrived at or below
// their bond's last seq (must be 0), a reconnect with lastEventId (how many replayed marks were stale: must be 0),
// and the overlay totals' server time on a cache MISS and HIT. The password and the tokens are never printed.

const base = process.env.BASE_URL ?? 'http://localhost:8080';
const email = process.env.DESK_EMAIL;
const password = process.env.DESK_PASSWORD;
const seconds = Number(process.argv[2] ?? 60);
if (!email || !password) {
  console.error('Set DESK_EMAIL and DESK_PASSWORD (create the account with Desk.UserAdmin).');
  process.exit(1);
}

const pct = (xs, p) => {
  if (xs.length === 0) return NaN;
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.ceil((p / 100) * s.length) - 1)];
};

const login = await fetch(`${base}/api/auth/login`, {
  method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password }),
});
if (!login.ok) throw new Error(`login: ${login.status}`);
const { accessToken } = await login.json();
const auth = { authorization: `Bearer ${accessToken}` };

/** Reads one stream for up to `ms`; returns what it saw. */
async function read(ms, lastEventId, last) {
  const stop = new AbortController();
  const timer = setTimeout(() => stop.abort(), ms);
  const started = performance.now();
  const url = `${base}/api/marks/stream${lastEventId === null ? '' : `?lastEventId=${lastEventId}`}`;
  const res = await fetch(url, { headers: auth, signal: stop.signal });
  if (res.status !== 200) throw new Error(`stream: ${res.status}`);
  const seen = { helloMs: null, hello: null, bytes: 0, marks: 0, stale: 0, latencies: [], max: lastEventId, bye: null };
  const decoder = new TextDecoder();
  let pending = '';
  try {
    for await (const chunk of res.body) {
      seen.bytes += chunk.length;
      pending += decoder.decode(chunk, { stream: true });
      const blocks = pending.split('\n\n');
      pending = blocks.pop();
      for (const block of blocks) {
        const event = /^event: (.*)$/m.exec(block)?.[1];
        const data = JSON.parse(/^data: (.*)$/m.exec(block)?.[1] ?? 'null');
        if (event === 'hello') {
          seen.hello = data;
          seen.helloMs = performance.now() - started;
        } else if (event === 'mark') {
          seen.marks++;
          if ((last.get(data.bondId) ?? -Infinity) >= data.seq) seen.stale++;
          else last.set(data.bondId, data.seq);
          if (data.seq > seen.hello.seq) seen.latencies.push(Date.now() - Date.parse(data.eventTs));
          seen.max = Math.max(seen.max ?? -Infinity, data.seq);
        } else if (event === 'bye') {
          seen.bye = data.reason;
        }
      }
    }
  } catch (e) {
    if (e.name !== 'AbortError') throw e;
  } finally {
    clearTimeout(timer);
  }
  return seen;
}

const last = new Map();
const first = await read(seconds * 1000, null, last);
const minutes = seconds / 60;
// Reconnect a few seconds later from the last seq: only what changed in between may come back.
await new Promise((r) => setTimeout(r, 3000));
const again = await read(2000, first.max, last);

console.log(`Mark stream, ${seconds} s, ${first.hello.bonds} bonds, ${first.hello.tickMs} ms ticks; access token ${accessToken.length} chars`);
console.log('');
console.log('| Measure | Value |');
console.log('|---|---|');
console.log(`| Time to hello (connect, auth, universe) | ${first.helloMs.toFixed(1)} ms |`);
console.log(`| Body bytes per minute | ${(first.bytes / minutes / 1024).toFixed(1)} KB |`);
console.log(`| Marks per minute | ${(first.marks / minutes).toFixed(0)} (incl. ${first.hello.bonds} on connect) |`);
console.log(`| Live mark latency p50 / p95 / max | ${pct(first.latencies, 50)} / ${pct(first.latencies, 95)} / ${Math.max(...first.latencies)} ms |`);
console.log(`| Marks at or below their bond's last seq | ${first.stale} |`);
console.log(`| Reconnect with lastEventId: time to hello | ${again.helloMs.toFixed(1)} ms |`);
console.log(`| Reconnect: marks replayed / stale among them | ${again.marks} / ${again.stale} |`);

// Overlay totals: distinct quick filters make distinct views, so each first call is a MISS; the repeat is a HIT.
const body = (q) => JSON.stringify({ columns: ['deal_name', 'price', 'spread_bp', 'market_value'], quickFilter: q });
const timing = (res) => Number(/total;dur=([\d.]+)/.exec(res.headers.get('server-timing') ?? '')?.[1]);
const miss = [];
const hit = [];
for (const q of ['', 'clo', 'rmbs', 'cmbs', 'abs', '2019', '2020', '2021', '2022', '2023', 'a1', 'a2', 'b', 'm1', 'senior', 'mezz', 'equity', 'aaa', 'bbb', 'x']) {
  for (let k = 0; k < 2; k++) {
    const res = await fetch(`${base}/api/marks/totals`, { method: 'POST', headers: { ...auth, 'content-type': 'application/json' }, body: body(q) });
    await res.arrayBuffer();
    if (res.status !== 200) throw new Error(`totals: ${res.status}`);
    (res.headers.get('x-cache') === 'HIT' ? hit : miss).push(timing(res));
  }
}
console.log(`| Overlay totals server time, MISS p50 / p95 (n=${miss.length}) | ${pct(miss, 50).toFixed(1)} / ${pct(miss, 95).toFixed(1)} ms |`);
console.log(`| Overlay totals server time, HIT p50 / p95 (n=${hit.length}) | ${pct(hit, 50).toFixed(1)} / ${pct(hit, 95).toFixed(1)} ms |`);
