// README §6 P2 / §10: fund performance latency, cache MISS vs HIT (budget: p95 < 300 ms).
// Local compose stack only (never Render/Neon/production):
//
//   docker run --rm -i --add-host=host.docker.internal:host-gateway \
//     -e BASE_URL=http://host.docker.internal:5182 -e DESK_EMAIL=… -e DESK_PASSWORD=… \
//     grafana/k6:2.3.0 run - < perf/funds.js
//
// DESK_EMAIL must be an admin: setup() clears the response cache so MISS really misses on a rerun.
// The API must run with the per-user limit raised for the run (RATE_LIMIT_PER_USER_PER_MIN/BURST): k6 is one user.
// MISS requests use a CUSTOM month pair that has never been asked for (the response cache is keyed by month-ends),
// so every one reads the database; HIT cycles funds 1–4 × ITD/YTD, cached after the first round.
import http from 'k6/http';
import { check } from 'k6';

const base = __ENV.BASE_URL || 'http://localhost:5182';

export const options = {
  scenarios: {
    miss: { executor: 'shared-iterations', vus: 1, iterations: 400, exec: 'miss' },
    // Paced: unpaced cache hits outrun even a raised per-user limit (a 429 fails the run).
    hit: { executor: 'constant-arrival-rate', rate: 50, timeUnit: '1s', duration: '20s', preAllocatedVUs: 2, exec: 'hit', startTime: '15s' },
  },
  thresholds: {
    checks: ['rate==1.0'], // a failing request must fail the run, not look fast
    'http_req_duration{scenario:miss}': ['p(95)<300'],
    'http_req_duration{scenario:hit}': ['p(95)<300'],
  },
  summaryTrendStats: ['p(50)', 'p(95)', 'max'],
};

export function setup() {
  const res = http.post(`${base}/api/auth/login`, JSON.stringify({ email: __ENV.DESK_EMAIL, password: __ENV.DESK_PASSWORD }),
    { headers: { 'Content-Type': 'application/json' } });
  check(res, { 'login 200': (r) => r.status === 200 });
  // Bearer token (ADR-0024): the whole run (~35 s) is inside the access token's 5 minutes.
  const authorization = `Bearer ${res.json('accessToken')}`;
  const clear = http.post(`${base}/api/admin/cache/clear`, null, { headers: { Authorization: authorization } });
  check(clear, { 'cache cleared (admin)': (r) => r.status === 204 });
  return { authorization };
}

function get(data, path, cache) {
  const res = http.get(`${base}${path}`, { headers: { 'Accept-Encoding': 'br, gzip', Authorization: data.authorization } });
  if (!check(res, { '200': (r) => r.status === 200 })) throw new Error(`HTTP ${res.status}: ${res.body}`);
  if (cache) check(res, { [cache]: (r) => r.headers['X-Cache'] === cache });
}

// Month index 0 = 2023-04, the first month every fund has data; 41 = 2026-09, the last seeded month.
const month = (i) => `${2023 + Math.floor((3 + i) / 12)}-${String(((3 + i) % 12) + 1).padStart(2, '0')}-01`;
// 4 funds × 100 distinct (from, to) pairs of 19–42 months, all inside every fund's data: never repeated in a run.
export function miss(data) {
  const fund = (__ITER % 4) + 1;
  const k = Math.floor(__ITER / 4);
  get(data, `/api/funds/${fund}/performance?range=CUSTOM&from=${month(k % 20)}&to=${month(41 - Math.floor(k / 20))}`, 'MISS');
}

const hits = [1, 2, 3, 4].flatMap((f) => ['ITD', 'YTD'].map((r) => `/api/funds/${f}/performance?range=${r}`));
let n = 0;
export function hit(data) {
  get(data, hits[n++ % hits.length], n > hits.length ? 'HIT' : undefined);
}
