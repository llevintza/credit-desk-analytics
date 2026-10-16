import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { brotliCompressSync, gzipSync } from 'node:zlib';
import { decodeBody, blockProblem, main } from './payload-size.mjs';

// #136: the budgets check fails closed. A 200 that is short, empty, missing columns or rows,
// or not the requested shape must fail, not print `ok` under the budget.
const SCRIPT = new URL('./payload-size.mjs', import.meta.url).pathname;

const block = (cols, rows) => ({
  columns: ['id', ...cols],
  data: ['id', ...cols].map(() => Array.from({ length: rows }, (_, i) => i)),
  rowCount: 18_000,
  summary: [],
});

test('decodeBody: brotli, gzip and identity bodies decode; an unknown encoding throws', () => {
  const json = Buffer.from('{"a":1}');
  assert.equal(decodeBody({ headers: { 'content-encoding': 'br' }, body: brotliCompressSync(json) }).toString(), '{"a":1}');
  assert.equal(decodeBody({ headers: { 'content-encoding': 'gzip' }, body: gzipSync(json) }).toString(), '{"a":1}');
  assert.equal(decodeBody({ headers: {}, body: json }).toString(), '{"a":1}');
  assert.equal(decodeBody({ headers: { 'content-encoding': 'identity' }, body: json }).toString(), '{"a":1}');
  assert.throws(() => decodeBody({ headers: { 'content-encoding': 'zstd' }, body: json }), /unsupported content-encoding zstd/);
});

test('blockProblem: a full block of n+1 columns and 200 rows passes', () => {
  assert.equal(blockProblem(block(['a', 'b'], 200), ['a', 'b']), null);
});

test('blockProblem: short, empty and mis-shaped blocks fail', () => {
  const cols = ['a', 'b'];
  const cases = [
    [null, /not an object/],
    [[], /not an object/],
    [{}, /columns/],
    [block(['a'], 200), /2 columns, expected 3/],
    [block(['a', 'b', 'c'], 200), /4 columns, expected 3/],
    [block(cols, 199), /199 rows in data\[0\], expected 200/],
    [block(cols, 0), /0 rows in data\[0\], expected 200/],
    [{ ...block(cols, 200), rowCount: 150 }, /rowCount 150, expected ≥ 200/],
    [{ ...block(cols, 200), rowCount: '18000' }, /rowCount "18000", expected ≥ 200/],
    [{ ...block(cols, 200), data: block(cols, 200).data.slice(1) }, /2 data columns, expected 3/],
    [{ ...block(cols, 200), data: 'x' }, /2 data columns|data is not an array/],
  ];
  for (const [b, re] of cases) assert.match(blockProblem(b, cols) ?? 'null', re, JSON.stringify(b)?.slice(0, 80));
  // One ragged column fails too.
  const ragged = block(cols, 200);
  ragged.data[2] = ragged.data[2].slice(0, 10);
  assert.match(blockProblem(ragged, cols), /data\[2\] has 10 rows, expected 200/);
});

// End to end: main() against a stub API. `opts` breaks one response at a time.
async function withStub(opts, fn) {
  const presets = opts.presets ?? [
    { name: 'Risk', state: { columns: ['a', 'b'] } },
    { name: 'All', state: { columns: ['a', 'b', 'c'] } },
  ];
  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', () => {
      const send = (status, obj, enc = 'br') => {
        const raw = Buffer.from(typeof obj === 'string' ? obj : JSON.stringify(obj));
        const out = enc === 'br' ? brotliCompressSync(raw) : raw;
        res.writeHead(status, { 'content-type': 'application/json', ...(enc === 'br' ? { 'content-encoding': 'br' } : {}) });
        res.end(out);
      };
      if (req.url === '/api/auth/login') return send(opts.loginStatus ?? 200, { accessToken: 't' }, 'identity');
      if (req.url === '/api/presets/positions') return opts.presetsStatus ? send(opts.presetsStatus, 'boom', 'identity') : send(200, presets, 'identity');
      if (req.url === '/api/positions/query') {
        const q = JSON.parse(body);
        const preset = q.columns.length === 2 ? 'Risk' : 'All';
        if (opts.queryStatus?.[preset]) return send(opts.queryStatus[preset], 'bad', 'identity');
        if (opts.queryBody !== undefined) return send(200, opts.queryBody);
        const b = block(q.columns, q.endRow - q.startRow);
        // Incompressible padding, sent as identity, pushes a preset over its budget.
        if (opts.padKb?.[preset]) return send(200, { ...b, pad: randomBytes(opts.padKb[preset] * 1024).toString('base64') }, 'identity');
        return send(200, b);
      }
      if (req.url.startsWith('/api/funds/1/performance')) {
        if (opts.fundPadKb) return send(200, { pad: randomBytes(opts.fundPadKb * 1024).toString('base64') }, 'identity');
        return send(200, opts.fundBody !== undefined ? opts.fundBody : { points: [1, 2] });
      }
      if (req.url.startsWith('/api/insights/')) return send(opts.insightsStatus ?? 200, { items: [] });
      send(404, 'nope', 'identity');
    });
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  try {
    return await fn(`http://127.0.0.1:${server.address().port}`);
  } finally {
    server.close();
  }
}

async function runAgainstStub(opts = {}) {
  return withStub(opts, async (url) => {
    const out = [];
    const err = [];
    const env = { BASE_URL: url, DESK_EMAIL: 'e', DESK_PASSWORD: 'p', ...opts.env };
    const code = await main(env, (l) => out.push(l), (l) => err.push(l));
    return { code, out: out.join('\n'), err: err.join('\n') };
  });
}

test('main: a well-formed stack passes and prints the Rows column', async () => {
  const r = await runAgainstStub();
  assert.equal(r.code, 0, r.err);
  assert.match(r.out, /\| Preset \| Columns \| Rows \| Encoding \|/);
  assert.match(r.out, /\| Risk \| 3 \| 200 \| br \|.*\| ok \|/);
  assert.match(r.out, /\| All \| 4 \| 200 \| br \|.*\| ok \|/);
  assert.match(r.out, /\| P3 insights reference \|/);
});

test('main: presets non-200 exits 2', async () => {
  const r = await runAgainstStub({ presetsStatus: 500 });
  assert.equal(r.code, 2);
  assert.match(r.err, /presets: HTTP 500/);
});

test('main: presets that are not an array, or lack a built-in preset, exit 2', async () => {
  assert.match((await runAgainstStub({ presets: { name: 'Risk' } })).err, /presets: body is not an array/);
  const r = await runAgainstStub({ presets: [{ name: 'Risk', state: { columns: [] } }] });
  assert.equal(r.code, 2);
  assert.match(r.err, /built-in preset Risk not found/);
});

test('main: an empty or short 200 query exits 2 (a broken query never prints ok)', async () => {
  for (const queryBody of [{}, '', block(['a', 'b'], 0), block(['a'], 200)]) {
    const r = await runAgainstStub({ queryBody });
    assert.equal(r.code, 2, `${JSON.stringify(queryBody).slice(0, 60)}: ${r.out}`);
    assert.match(r.err, /Risk: unexpected block|Risk: body is not JSON/);
    assert.doesNotMatch(r.out, /\| Risk \|.*\| ok \|/);
  }
});

test('main: a non-200 query, fund or insights call exits 2', async () => {
  assert.match((await runAgainstStub({ queryStatus: { All: 400 } })).err, /All: HTTP 400/);
  assert.match((await runAgainstStub({ insightsStatus: 503 })).err, /P3 core: HTTP 503/);
  const r = await runAgainstStub({ fundBody: 'not json' });
  assert.equal(r.code, 2);
  assert.match(r.err, /P2 fund 1 ITD: body is not JSON/);
  assert.match((await runAgainstStub({ fundBody: 5 })).err, /P2 fund 1 ITD: body is not an object/);
});

test('main: missing credentials or a failed login exit 2', async () => {
  assert.match((await runAgainstStub({ env: { DESK_PASSWORD: '' } })).err, /Set DESK_EMAIL and DESK_PASSWORD/);
  const r = await runAgainstStub({ loginStatus: 401 });
  assert.equal(r.code, 2);
  assert.match(r.err, /login failed: HTTP 401/);
});

test('main: Risk over 60 KB fails (exit 1); All over 250 KB only warns', async () => {
  const risk = await runAgainstStub({ padKb: { Risk: 70 } });
  assert.equal(risk.code, 1);
  assert.match(risk.out, /\| Risk \|.*\| identity \|.*\| 60 \| FAIL \|/);
  const all = await runAgainstStub({ padKb: { All: 260 } });
  assert.equal(all.code, 0);
  assert.match(all.out, /\| All \|.*\| WARN \|/);
  assert.match(all.out, /::warning::All first block is .* KB, over the 250 KB budget/);
});

test('main: fund 1 ITD at 5 KB or more fails (exit 1)', async () => {
  const r = await runAgainstStub({ fundPadKb: 6 });
  assert.equal(r.code, 1);
  assert.match(r.out, /\| P2 fund 1 ITD \|.*\| 5 \| FAIL \|/);
});

test('cli: process exit code is main()\'s (a broken stack exits 2)', async () => {
  const code = await withStub({ presetsStatus: 500 }, (url) =>
    new Promise((resolve) => {
      const child = spawn(process.execPath, [SCRIPT], {
        env: { PATH: process.env.PATH, BASE_URL: url, DESK_EMAIL: 'e', DESK_PASSWORD: 'p' },
        stdio: 'ignore',
      });
      child.on('close', resolve);
    }),
  );
  assert.equal(code, 2);
});
