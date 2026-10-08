// README §10: P1 API latency, cache MISS vs HIT (budgets: p95 ≤ 150 ms MISS, ≤ 15 ms HIT, local, warm).
//
//   docker run --rm -i --add-host=host.docker.internal:host-gateway \
//     -e BASE_URL=http://host.docker.internal:5181 -e DESK_EMAIL=… -e DESK_PASSWORD=… \
//     grafana/k6:1.3.0 run - < perf/positions.js
//
// The API must run with the per-user limit raised for the run (RATE_LIMIT_PER_USER_PER_MIN/BURST): k6 is one user.
// MISS requests are the whole book under a filter value that has never been asked for, so neither the block nor the
// summary is cached: the first block of a brand-new view over every row, the worst case (#129). Run at scale 1.0
// (the 'whole book' check expects ~20k rows). HIT repeats one request. Local stack only.
import http from 'k6/http';
import { check } from 'k6';

const base = __ENV.BASE_URL || 'http://localhost:5181';
const risk = [
  'deal_name', 'class', 'cusip', 'sector', 'rating_composite', 'current_face', 'market_value', 'unrealized_pnl',
  'pct_of_portfolio_mv', 'price', 'price_chg_1d', 'yield', 'spread_bp', 'oas_bp', 'dm_bp', 'z_spread_bp',
  'spread_chg_1d_bp', 'vendor_dispersion_bp', 'mod_duration', 'eff_duration', 'convexity', 'dv01', 'krd_2y',
  'krd_5y', 'krd_10y', 'krd_20y', 'krd_30y', 'spread_duration', 'cs01', 'jtd', 'expected_loss_pct',
  'credit_enhancement_pct', 'wal', 'coupon_current', 'pnl_carry', 'pnl_rates', 'pnl_spread', 'pnl_total_mtd',
  'worst_case_price', 'stress_loss_mv', 'watchlist_flag',
];

export const options = {
  scenarios: {
    miss: { executor: 'constant-vus', vus: 1, duration: '30s', exec: 'miss' },
    hit: { executor: 'constant-vus', vus: 1, duration: '30s', exec: 'hit', startTime: '31s' },
  },
  thresholds: {
    checks: ['rate==1.0'], // a failing request must fail the run, not look fast
    'http_req_duration{scenario:miss}': ['p(95)<150'],
    'http_req_duration{scenario:hit}': ['p(95)<15'],
  },
  summaryTrendStats: ['p(50)', 'p(95)', 'max'],
};

export function setup() {
  const res = http.post(`${base}/api/auth/login`, JSON.stringify({ email: __ENV.DESK_EMAIL, password: __ENV.DESK_PASSWORD }),
    { headers: { 'Content-Type': 'application/json' } });
  check(res, { 'login 200': (r) => r.status === 200 });
  // Build the header here: cookie objects lose their shape when setup() data is handed to the VUs.
  return {
    cookie: Object.entries(res.cookies).map(([name, values]) => `${name}=${values[0].value}`).join('; '),
    xsrf: decodeURIComponent(res.cookies['XSRF-TOKEN'][0].value),
  };
}

function post(data, body) {
  // Every cookie from login (session, antiforgery cookie, XSRF-TOKEN) goes as a header: the jar won't send the
  // Secure __Host- cookie over plain http.
  const res = http.post(`${base}/api/positions/query`, JSON.stringify(body), {
    headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': data.xsrf, 'Accept-Encoding': 'br, gzip', Cookie: data.cookie },
  });
  if (!check(res, { '200': (r) => r.status === 200 })) throw new Error(`HTTP ${res.status}: ${res.body}`);
  return res;
}

let counter = 0;
export function miss(data) {
  counter += 1;
  // The worst case (#129): the whole book's first view, uncached. A threshold below every spread keeps every row,
  // and is never repeated (per VU, per iteration), so neither the block nor the summary is cached.
  const threshold = -1e6 * __VU - counter;
  const res = post(data, {
    columns: risk, sortModel: [{ colId: 'market_value', sort: 'desc' }],
    filterModel: { spread_bp: { filterType: 'number', type: 'greaterThan', filter: threshold } },
  });
  check(res, {
    'MISS': (r) => r.headers['X-Cache'] === 'MISS',
    'whole book': (r) => r.json('rowCount') >= 19000, // scale 1.0: ~20k positions per as-of
  });
}

export function hit(data) {
  const res = post(data, { columns: risk, sortModel: [{ colId: 'market_value', sort: 'desc' }] });
  check(res, { 'HIT': (r) => r.headers['X-Cache'] === 'HIT' }); // a request that misses fails checks: rate==1.0
}
