import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { brotliCompressSync, constants } from 'node:zlib';

// #348: the CI budgets job runs this script, so it must fail closed: a file whose wire bytes exceed the
// 16 KB-slice estimate fails, and so does a directory with no .js/.css (a vacuous pass) or a non-br response.
const SCRIPT = new URL('./bundle-wire-bytes.mjs', import.meta.url).pathname;
const q = (b, quality) => brotliCompressSync(b, { params: { [constants.BROTLI_PARAM_QUALITY]: quality } });
// Deterministic, mildly compressible content over several 16 KB slices.
const content = (n) => Buffer.from(Array.from({ length: n }, (_, i) => `const v${i % 997} = ${(i * 7919) % 104729};\n`).join(''));

async function run(files, serve) {
  const dir = mkdtempSync(join(tmpdir(), 'wire-'));
  for (const [name, body] of Object.entries(files)) writeFileSync(join(dir, name), body);
  const server = http.createServer((req, res) => {
    const name = req.url.slice(1);
    const body = files[name];
    if (!body) return res.writeHead(404).end();
    const { status = 200, encoding = 'br', bytes } = serve(name, body);
    res.writeHead(status, encoding ? { 'content-encoding': encoding } : {});
    res.end(bytes);
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  try {
    return await new Promise((resolve) => {
      const child = spawn(process.execPath, [SCRIPT, dir], { env: { PATH: process.env.PATH, BASE_URL: `http://127.0.0.1:${server.address().port}` } });
      let out = '';
      let err = '';
      child.stdout.on('data', (c) => (out += c));
      child.stderr.on('data', (c) => (err += c));
      child.on('close', (code) => resolve({ code, out, err }));
    });
  } finally {
    server.close();
  }
}

const files = { 'main.js': content(4000), 'styles.css': content(500), 'index.html': Buffer.from('<html></html>') };

test('wire bytes at or under the estimate pass and list every .js/.css file', async () => {
  // A one-shot q11 body is never bigger than q1 in 16 KB slices.
  const r = await run(files, (_, body) => ({ bytes: q(body, 11) }));
  assert.equal(r.code, 0, r.err);
  assert.match(r.out, /\| main\.js \|/);
  assert.match(r.out, /\| styles\.css \|/);
  assert.doesNotMatch(r.out, /index\.html|UNDER/);
});

test('wire bytes over the estimate fail (exit 1, UNDER)', async () => {
  const r = await run(files, (name, body) => ({ bytes: name === 'main.js' ? Buffer.concat([q(body, 1), Buffer.alloc(64 * 1024)]) : q(body, 11) }));
  assert.equal(r.code, 1);
  assert.match(r.out, /\| main\.js \|.*\(UNDER\)/);
  assert.match(r.err, /FAIL: the 16 KB-slice measure is under the wire bytes/);
});

test('a directory with no .js or .css fails instead of passing vacuously', async () => {
  const r = await run({ 'index.html': Buffer.from('<html></html>') }, (_, body) => ({ bytes: body }));
  assert.equal(r.code, 1);
  assert.match(r.err, /no \.js or \.css files in /);
});

test('a response that is not br-encoded fails', async () => {
  const r = await run(files, (_, body) => ({ bytes: body, encoding: null }));
  assert.notEqual(r.code, 0);
  assert.match(r.err, /content-encoding undefined/);
});
