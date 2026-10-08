// README §10: P1 API latency, cache MISS vs HIT (budgets: p95 ≤ 150 ms MISS, ≤ 15 ms HIT, local, warm).
//
//   docker run --rm -i --add-host=host.docker.internal:host-gateway \
//     -e BASE_URL=http://host.docker.internal:5181 -e DESK_EMAIL=… -e DESK_PASSWORD=… \
//     grafana/k6:1.3.0 run - < perf/positions.js
//
// The API must run with the per-user limit raised for the run (RATE_LIMIT_PER_USER_PER_MIN/BURST): k6 is one user.
// MISS requests are the whole book under a filter value that has never been asked for, so neither the block nor the
// summary is cached: the first block of a brand-new view over every row, the worst case for the Risk preset (#129).
// setup() counts the book once (unfiltered, which also warms the HIT key); every MISS must return exactly that many
// rows, and the 'book >= MIN_BOOK' check needs at least MIN_BOOK (a whole number, default 19000) of them. HIT repeats
// that one request.
// Cached blocks and summaries live until the next batch, so MISS filter values carry a per-run offset from setup():
// back-to-back runs against the same API stay MISS without a restart or a cache clear. Local stack only.
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const base = __ENV.BASE_URL || 'http://localhost:5181';
const risk = [
  'deal_name', 'class', 'cusip', 'sector', 'rating_composite', 'current_face', 'market_value', 'unrealized_pnl',
  'pct_of_portfolio_mv', 'price', 'price_chg_1d', 'yield', 'spread_bp', 'oas_bp', 'dm_bp', 'z_spread_bp',
  'spread_chg_1d_bp', 'vendor_dispersion_bp', 'mod_duration', 'eff_duration', 'convexity', 'dv01', 'krd_2y',
  'krd_5y', 'krd_10y', 'krd_20y', 'krd_30y', 'spread_duration', 'cs01', 'jtd', 'expected_loss_pct',
  'credit_enhancement_pct', 'wal', 'coupon_current', 'pnl_carry', 'pnl_rates', 'pnl_spread', 'pnl_total_mtd',
  'worst_case_price', 'stress_loss_mv', 'watchlist_flag',
];

// Fails closed: a blank or non-numeric MIN_BOOK stops the run instead of turning the guard off.
const minBookRaw = (__ENV.MIN_BOOK || '19000').trim();
if (!/^\d+$/.test(minBookRaw)) throw new Error(`MIN_BOOK must be a whole number, got '${__ENV.MIN_BOOK}'`);
const MIN_BOOK = Number(minBookRaw);

// The unfiltered view: setup() counts it (and so warms the HIT key); MISS adds a never-seen filter to it.
const wholeBook = { columns: risk, sortModel: [{ colId: 'market_value', sort: 'desc' }] };

export const options = {
  scenarios: {
    miss: { executor: 'constant-vus', vus: 1, duration: '30s', exec: 'miss' },
    hit: { executor: 'constant-vus', vus: 1, duration: '30s', exec: 'hit', startTime: '31s' },
    all: { executor: 'constant-vus', vus: 1, duration: '30s', exec: 'all', startTime: '62s' },
  },
  thresholds: {
    checks: ['rate==1.0'], // a failing request must fail the run, not look fast
    'http_req_duration{scenario:miss}': ['p(95)<150'],
    'http_req_duration{scenario:hit}': ['p(95)<15'],
    all_miss: ['p(95)<150'], // the same MISS budget for the All preset (#135)
  },
  summaryTrendStats: ['p(50)', 'p(95)', 'max'],
};

export function setup() {
  const res = http.post(`${base}/api/auth/login`, JSON.stringify({ email: __ENV.DESK_EMAIL, password: __ENV.DESK_PASSWORD }),
    { headers: { 'Content-Type': 'application/json' } });
  check(res, { 'login 200': (r) => r.status === 200 });
  // Build the header here: cookie objects lose their shape when setup() data is handed to the VUs.
  const data = {
    cookie: Object.entries(res.cookies).map(([name, values]) => `${name}=${values[0].value}`).join('; '),
    xsrf: decodeURIComponent(res.cookies['XSRF-TOKEN'][0].value),
    run: Date.now() % 1e6, // a per-run offset, so no MISS key repeats one from an earlier run (#202)
  };
  data.book = post(data, wholeBook).json('rowCount');
  check(data.book, { 'book >= MIN_BOOK': (n) => n >= MIN_BOOK });
  return data;
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
  // The worst case for the Risk preset (#129): the whole book's first view, uncached. A threshold below every spread
  // keeps every row, and is never repeated (per VU, per iteration, per run), so neither the block nor the summary is
  // cached. Every term stays far below 2^53, so each value is exact.
  const threshold = -1e12 * __VU - 1e6 * data.run - counter;
  const res = post(data, {
    ...wholeBook,
    filterModel: { spread_bp: { filterType: 'number', type: 'greaterThan', filter: threshold } },
  });
  check(res, {
    'MISS': (r) => r.headers['X-Cache'] === 'MISS',
    'whole book': (r) => r.json('rowCount') === data.book,
  });
}

export function hit(data) {
  // The exact request setup() warmed, so every one is a HIT; a request that misses fails checks: rate==1.0.
  const res = post(data, wholeBook);
  check(res, { 'HIT': (r) => r.headers['X-Cache'] === 'HIT' });
}

// The All preset's whole-book first view, uncached (#135): the widest P1 request (~200 columns). Its own trend, so
// the one-off preset lookup below stays out of the number.
const allMiss = new Trend('all_miss', true);
const allRun = Date.now(); // per VU and per run, so a rerun against the same API still misses
let allColumns;
let allCounter = 0;
export function all(data) {
  if (!allColumns) {
    const res = http.get(`${base}/api/presets/positions`, { headers: { Cookie: data.cookie } });
    allColumns = res.json().find((p) => p.name === 'All').state.columns;
  }
  allCounter += 1;
  // A fractional threshold below every spread: the whole book, never asked for before (miss() uses whole numbers).
  const threshold = -1e13 - allRun - allCounter - 0.5;
  const res = post(data, {
    ...wholeBook, columns: allColumns,
    filterModel: { spread_bp: { filterType: 'number', type: 'greaterThan', filter: threshold } },
  });
  allMiss.add(res.timings.duration);
  check(res, {
    'All MISS': (r) => r.headers['X-Cache'] === 'MISS',
    'All whole book': (r) => r.json('rowCount') === data.book,
  });
}
