import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { apiRows, cspRows, main, mainScript, metaContent, pagesRows, parseArgs, parseCsp, table } from './security-headers.mjs';

// #66: the header check fails closed. A missing or loosened header fails, a page that isn't a 200 is exit 2,
// and the Pages copy is held to its meta CSP, meta referrer and frame guard.
const APP_CSP =
  "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
  "font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
const API_HEADERS = {
  'content-security-policy': APP_CSP,
  'x-content-type-options': 'nosniff',
  'x-frame-options': 'DENY',
  'referrer-policy': 'same-origin',
  'permissions-policy': 'accelerometer=(), camera=(), geolocation=(), microphone=()',
  'cross-origin-opener-policy': 'same-origin',
};
const API = 'https://api.example.test';
const pagesHtml = (csp, referrer = 'same-origin') =>
  `<!doctype html><html><head><meta charset="utf-8">\n  <meta http-equiv="Content-Security-Policy" content="${csp}">` +
  `<meta name="referrer" content="${referrer}"></head><body><script src="main-AB12CD34.js" type="module"></script></body></html>`;
const PAGES_CSP = APP_CSP.replace("connect-src 'self'", `connect-src 'self' ${API}`).replace("; frame-ancestors 'none'", '');
const failed = (rows) => rows.filter((r) => r.ok === false).map((r) => r.check);

test('parseCsp: directives by lower-cased name, first one wins, blanks ignored', () => {
  const csp = parseCsp("Script-Src 'self' ; ;script-src *; img-src 'self' data:");
  assert.deepEqual(csp.get('script-src'), ["'self'"]);
  assert.deepEqual(csp.get('img-src'), ["'self'", 'data:']);
  assert.equal(parseCsp(undefined).size, 0);
});

test('apiRows: the app header set passes, with HSTS informational over http', () => {
  const rows = apiRows('api /', new Headers(API_HEADERS), { https: false });
  assert.deepEqual(failed(rows), []);
  assert.equal(rows.at(-1).ok, null);
  assert.equal(rows.at(-1).got, 'not sent over http');
});

test('apiRows: over https HSTS must be at least a year', () => {
  const short = apiRows('api', new Headers({ ...API_HEADERS, 'strict-transport-security': 'max-age=2592000' }), { https: true });
  assert.deepEqual(failed(short), ['HSTS max-age ≥ 31536000']);
  const year = apiRows('api', new Headers({ ...API_HEADERS, 'strict-transport-security': 'max-age=31536000' }), { https: true });
  assert.deepEqual(failed(year), []);
  assert.deepEqual(failed(apiRows('api', new Headers(API_HEADERS), { https: true })), ['HSTS max-age ≥ 31536000']);
});

test('apiRows: every missing header fails', () => {
  assert.deepEqual(failed(apiRows('api', new Headers(), { https: false })), [
    "CSP default-src 'self'", "CSP script-src 'self' only", "CSP object-src 'none'", "CSP base-uri 'self'",
    'CSP has no unsafe-eval', "CSP 'unsafe-inline' only in style-src", "CSP connect-src 'self'", "CSP frame-ancestors 'none'",
    'X-Content-Type-Options nosniff', 'X-Frame-Options DENY', 'Referrer-Policy same-origin',
    'Permissions-Policy denies camera, microphone, geolocation', 'Cross-Origin-Opener-Policy same-origin',
  ]);
});

test('cspRows: a loosened policy fails the directive it loosens', () => {
  const loose = (from, to) => failed(cspRows('t', APP_CSP.replace(from, to), { frameAncestors: true }));
  assert.deepEqual(loose("script-src 'self'", "script-src 'self' https://cdn.example.test"), ["CSP script-src 'self' only"]);
  assert.deepEqual(loose("script-src 'self'", "script-src 'self' 'unsafe-inline'"),
    ["CSP script-src 'self' only", "CSP 'unsafe-inline' only in style-src"]);
  assert.deepEqual(loose("style-src 'self' 'unsafe-inline'", "style-src 'self' 'unsafe-inline' 'unsafe-eval'"), ['CSP has no unsafe-eval']);
  assert.deepEqual(loose("frame-ancestors 'none'", "frame-ancestors 'self'"), ["CSP frame-ancestors 'none'"]);
  assert.deepEqual(loose("connect-src 'self'", "connect-src 'self' https:"), ["CSP connect-src 'self'"]);
  assert.deepEqual(loose("object-src 'none'", "object-src 'self'"), ["CSP object-src 'none'"]);
});

test('metaContent and mainScript read the tags the Pages build writes', () => {
  const html = pagesHtml(PAGES_CSP);
  assert.equal(metaContent(html, 'http-equiv', 'content-security-policy'), PAGES_CSP);
  assert.equal(metaContent(html, 'name', 'referrer'), 'same-origin');
  assert.equal(metaContent(html, 'name', 'robots'), undefined);
  assert.equal(mainScript(html), 'main-AB12CD34.js');
  assert.equal(mainScript('<script src="polyfills.js"></script>'), undefined);
});

test('pagesRows: the Pages copy passes with its meta CSP, referrer and frame guard; platform headers are info', () => {
  const rows = pagesRows('pages', pagesHtml(PAGES_CSP), {
    apiOrigin: API, bundle: 'x("Credit Desk Analytics cannot be shown inside a frame.")', headers: new Headers({ 'x-frame-options': 'SAMEORIGIN' }),
  });
  assert.deepEqual(failed(rows), []);
  const platform = rows.filter((r) => r.ok === null);
  assert.equal(platform.length, 4);
  assert.deepEqual(platform.find((r) => r.check.endsWith('x-frame-options')).got, 'SAMEORIGIN');
  assert.deepEqual(platform.find((r) => r.check.endsWith('referrer-policy')).got, '(not sent)');
});

test('pagesRows: no meta CSP, the wrong API origin, no referrer meta or no frame guard fail', () => {
  assert.deepEqual(failed(pagesRows('p', '<html></html>', { apiOrigin: API, bundle: undefined, headers: new Headers() })), [
    'meta CSP present', "CSP default-src 'self'", "CSP script-src 'self' only", "CSP object-src 'none'", "CSP base-uri 'self'",
    'CSP has no unsafe-eval', "CSP 'unsafe-inline' only in style-src", `CSP connect-src 'self' ${API}`,
    'meta referrer same-origin', 'frame guard in the main bundle',
  ]);
  const other = pagesRows('p', pagesHtml(PAGES_CSP, 'no-referrer'), { apiOrigin: 'https://other.example.test', bundle: 'nothing', headers: new Headers() });
  assert.deepEqual(failed(other), ["CSP connect-src 'self' https://other.example.test", 'meta referrer same-origin', 'frame guard in the main bundle']);
  assert.equal(other.find((r) => r.check.startsWith('frame guard')).got, 'absent');
});

test('table: escapes pipes and marks fail, ok and info', () => {
  const t = table([{ target: 'a', check: 'c|d', got: 'x', ok: false }, { target: 'b', check: 'e', got: 'y', ok: true }, { target: 'c', check: 'f', got: 'z', ok: null }]);
  assert.match(t, /\| a \| c\\\|d \| `x` \| \*\*FAIL\*\* \|/);
  assert.match(t, /\| b \| e \| `y` \| ok \|/);
  assert.match(t, /\| c \| f \| `z` \| info \|/);
});

test('parseArgs: --api is required, both take http(s) URLs, the API is reduced to its origin', () => {
  assert.deepEqual(parseArgs(['--api', 'http://localhost:8080/x/', '--pages', 'http://localhost:4300/a/']),
    { api: 'http://localhost:8080', pages: 'http://localhost:4300/a/' });
  assert.throws(() => parseArgs([]), /--api <origin> is required/);
  assert.throws(() => parseArgs(['--api']), /--api needs an http\(s\) URL/);
  assert.throws(() => parseArgs(['--api', 'ftp://x']), /--api needs an http\(s\) URL/);
  assert.throws(() => parseArgs(['--nope', 'x']), /unknown argument --nope/);
});

async function serve(routes) {
  const server = http.createServer((req, res) => {
    const r = routes[req.url];
    if (!r) return res.writeHead(404).end();
    res.writeHead(r.status ?? 200, r.headers ?? {}).end(r.body ?? '');
  });
  await new Promise((ok) => server.listen(0, '127.0.0.1', ok));
  return { origin: `http://127.0.0.1:${server.address().port}`, close: () => server.close() };
}

async function run(argv) {
  const lines = [];
  const code = await main(argv, (l) => lines.push(l));
  return { code, out: lines.join('\n') };
}

test('main: a good API and Pages copy exit 0; a missing header exits 1', async () => {
  const api = await serve({ '/health': { headers: API_HEADERS }, '/': { headers: API_HEADERS, body: '<html></html>' } });
  const pages = await serve({
    '/app/': { body: pagesHtml(PAGES_CSP.replace(API, api.origin)) },
    '/app/main-AB12CD34.js': { body: 'cannot be shown inside a frame' },
  });
  const bad = await serve({ '/health': { headers: API_HEADERS }, '/': { headers: { ...API_HEADERS, 'x-frame-options': 'SAMEORIGIN' } } });
  try {
    const good = await run(['--api', api.origin, '--pages', `${pages.origin}/app/`]);
    assert.equal(good.code, 0, good.out);
    assert.match(good.out, /All checks passed\./);
    assert.match(good.out, /\| pages \| frame guard in the main bundle \| `present` \| ok \|/);
    const worse = await run(['--api', bad.origin]);
    assert.equal(worse.code, 1);
    assert.match(worse.out, /\| api \/ \| X-Frame-Options DENY \| `SAMEORIGIN` \| \*\*FAIL\*\* \|/);
    assert.match(worse.out, /1 check\(s\) failed\./);
  } finally {
    api.close();
    pages.close();
    bad.close();
  }
});

test('main: a non-200 page, an unreachable origin or bad arguments exit 2', async () => {
  const api = await serve({ '/health': { status: 503 } });
  try {
    const down = await run(['--api', api.origin]);
    assert.equal(down.code, 2);
    assert.match(down.out, /answered 503/);
  } finally {
    api.close();
  }
  assert.equal((await run(['--api', 'http://127.0.0.1:1'])).code, 2);
  const args = await run([]);
  assert.equal(args.code, 2);
  assert.match(args.out, /--api <origin> is required/);
});
