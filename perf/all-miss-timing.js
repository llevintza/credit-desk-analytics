// #241: where the All preset's whole-book MISS time goes, from the API's own Server-Timing (db, ser, total) next to
// k6's wall time. The same request as perf/positions.js all(), uncached on every iteration. Local stack only.
//
//   docker run --rm -i --add-host=host.docker.internal:host-gateway \
//     -e BASE_URL=http://host.docker.internal:5181 -e DESK_EMAIL=… -e DESK_PASSWORD=… \
//     grafana/k6:2.3.0 run --quiet - < perf/all-miss-timing.js
//
// The API must run with the per-user limit raised for the run (RATE_LIMIT_PER_USER_PER_MIN/BURST): k6 is one user.
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const base = __ENV.BASE_URL || 'http://localhost:5181';
const trends = {
  db: new Trend('timing_db', true),
  ser: new Trend('timing_ser', true),
  total: new Trend('timing_total', true),
  wall: new Trend('timing_wall', true),
};

export const options = {
  vus: 1,
  duration: '30s',
  thresholds: { checks: ['rate==1.0'] },
  summaryTrendStats: ['p(50)', 'p(95)'],
};

export function setup() {
  const res = http.post(`${base}/api/auth/login`, JSON.stringify({ email: __ENV.DESK_EMAIL, password: __ENV.DESK_PASSWORD }),
    { headers: { 'Content-Type': 'application/json' } });
  check(res, { 'login 200': (r) => r.status === 200 });
  const authorization = `Bearer ${res.json('accessToken')}`;
  const columns = http.get(`${base}/api/presets/positions`, { headers: { Authorization: authorization } })
    .json().find((p) => p.name === 'All').state.columns;
  return { authorization, columns, run: Date.now() };
}

let counter = 0;
export default function (data) {
  counter += 1;
  // A threshold below every spread that no other script or iteration uses (positions.js uses -1e12… and -1e13…).
  const threshold = -2e13 - data.run - counter - 0.25;
  const res = http.post(`${base}/api/positions/query`, JSON.stringify({
    columns: data.columns,
    sortModel: [{ colId: 'market_value', sort: 'desc' }],
    filterModel: { spread_bp: { filterType: 'number', type: 'greaterThan', filter: threshold } },
  }), { headers: { 'Content-Type': 'application/json', 'Accept-Encoding': 'br, gzip', Authorization: data.authorization } });
  check(res, { '200 MISS': (r) => r.status === 200 && r.headers['X-Cache'] === 'MISS' });
  const timing = res.headers['Server-Timing'] || '';
  for (const key of ['db', 'ser', 'total']) {
    const m = timing.match(new RegExp(`${key};dur=([0-9.]+)`));
    if (m) trends[key].add(Number(m[1]));
  }
  trends.wall.add(res.timings.duration);
}

export function handleSummary(data) {
  const lines = ['All MISS (ms): Server-Timing db / ser / total, then k6 wall'];
  for (const key of ['db', 'ser', 'total', 'wall']) {
    const v = data.metrics[`timing_${key}`]?.values;
    lines.push(v ? `${key}: p50 ${v['p(50)'].toFixed(1)}, p95 ${v['p(95)'].toFixed(1)}` : `${key}: n/a`);
  }
  const checks = data.metrics.checks?.values;
  if (checks) lines.push(`checks: ${checks.passes} passed, ${checks.fails} failed`);
  return { stdout: `${lines.join('\n')}\n` };
}
