#!/usr/bin/env node
// ADR-0014 (#67): fills the API's response cache (PositionsCache, POSITIONS_CACHE_MB) with distinct whole-catalog
// grid blocks, so the instance's memory can be read with the cache at its size limit. LOCAL STACK ONLY, with a
// throwaway account created there with Desk.UserAdmin, and the per-user limits raised for the run (it sends hundreds
// of requests a minute); never against Render, Neon or production:
//   BASE_URL=http://localhost:8080 DESK_EMAIL=… DESK_PASSWORD=… node perf/cache-memory.mjs [--mb 160] [--parallel 4]
// --mb: uncompressed response bytes to request (default 160, well past the 64 MB default limit).
// Each block is 500 rows of every catalog column (the All preset's shape, the largest a user can ask for), keyed
// apart by its sort column and start row, so every request is a cache MISS that stores a new entry.

import { pathToFileURL } from 'node:url';

export const BLOCK_ROWS = 500;

/** Requests in sort-column-major order: every block of the book under one sort, then the next sort column. */
export function* blocks(columns, totalRows) {
  for (const colId of columns)
    for (let startRow = 0; startRow < totalRows; startRow += BLOCK_ROWS)
      yield { columns, startRow, endRow: startRow + BLOCK_ROWS, sortModel: [{ colId, sort: 'asc' }] };
}

function arg(argv, name, fallback) {
  const at = argv.indexOf(name);
  if (at < 0) return fallback;
  const value = Number(argv[at + 1]);
  if (!(value > 0)) throw new Error(`${name} must be a positive number`);
  return value;
}

async function main(env = process.env, argv = process.argv.slice(2)) {
  const base = new URL(env.BASE_URL ?? 'http://localhost:8080');
  if (!/^(localhost|127\.0\.0\.1|\[::1\])$/.test(base.hostname)) throw new Error('local stacks only (BASE_URL must be localhost)');
  if (!env.DESK_EMAIL || !env.DESK_PASSWORD) throw new Error('Set DESK_EMAIL and DESK_PASSWORD (create the account with Desk.UserAdmin).');
  const targetBytes = arg(argv, '--mb', 160) * 1024 * 1024;
  const parallel = arg(argv, '--parallel', 4);

  const login = await fetch(new URL('/api/auth/login', base), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ email: env.DESK_EMAIL, password: env.DESK_PASSWORD }),
  });
  if (login.status !== 200) throw new Error(`login failed: HTTP ${login.status}`);
  const headers = { authorization: `Bearer ${(await login.json()).accessToken}`, 'content-type': 'application/json' };

  const catalog = await (await fetch(new URL('/api/meta/columns', base), { headers })).json();
  const columns = catalog.map((c) => c.name).filter((n) => n !== 'position_id');
  const first = await fetch(new URL('/api/positions/query', base), { method: 'POST', headers, body: JSON.stringify({ columns, endRow: 1 }) });
  const totalRows = (await first.json()).rowCount;

  const queue = blocks(columns, totalRows);
  let bytes = 0;
  let requests = 0;
  const statuses = {};
  const started = Date.now();
  async function worker() {
    while (bytes < targetBytes) {
      const next = queue.next();
      if (next.done) return;
      const res = await fetch(new URL('/api/positions/query', base), { method: 'POST', headers, body: JSON.stringify(next.value) });
      const body = await res.arrayBuffer(); // fetch decodes br/gzip: this is the size the cache stores
      statuses[`${res.status} ${res.headers.get('x-cache') ?? '-'}`] = (statuses[`${res.status} ${res.headers.get('x-cache') ?? '-'}`] ?? 0) + 1;
      requests++;
      if (res.status === 200 && res.headers.get('x-cache') === 'MISS') bytes += body.byteLength;
    }
  }
  await Promise.all(Array.from({ length: parallel }, worker));
  console.log(`CACHE_FILL requests=${requests} miss_mb=${(bytes / 1024 / 1024).toFixed(1)} seconds=${((Date.now() - started) / 1000).toFixed(1)} ${JSON.stringify(statuses)}`);
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((e) => {
    console.error(`ERROR: ${e.message}`);
    process.exit(1);
  });
}
