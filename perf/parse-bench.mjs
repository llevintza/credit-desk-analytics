#!/usr/bin/env node
// ADR-0007: client-side cost of each wire format, on the payloads written by perf/GridBenchmark.
//   (cd perf && npm ci) && node perf/parse-bench.mjs perf/out [iterations=500]
// Times JSON.parse vs @msgpack/msgpack decode, plus turning the decoded document into AG Grid row objects
// (the columnar formats need a transpose; row JSON is already rows).
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { decode } from '@msgpack/msgpack';

const dir = process.argv[2] ?? 'perf/out';
const n = Number(process.argv[3] ?? 500);
const pct = (xs, p) => [...xs].sort((a, b) => a - b)[Math.min(xs.length - 1, Math.ceil((p / 100) * xs.length) - 1)];

const toRows = (doc) => {
  const { columns, data } = doc;
  const rows = new Array(data[0].length);
  for (let r = 0; r < rows.length; r++) {
    const row = {};
    for (let c = 0; c < columns.length; c++) row[columns[c]] = data[c][r];
    rows[r] = row;
  }
  return rows;
};

function bench(label, parse, toGridRows) {
  for (let i = 0; i < 50; i++) toGridRows(parse());
  const parseMs = [], totalMs = [];
  for (let i = 0; i < n; i++) {
    const t0 = performance.now();
    const doc = parse();
    const t1 = performance.now();
    toGridRows(doc);
    totalMs.push(performance.now() - t0);
    parseMs.push(t1 - t0);
  }
  console.log(`| ${label.padEnd(28)} | ${pct(parseMs, 50).toFixed(2).padStart(6)} | ${pct(totalMs, 50).toFixed(2).padStart(6)} | ${pct(totalMs, 95).toFixed(2).padStart(6)} |`);
}

console.log(`node ${process.version}, ${n} iterations`);
for (const preset of ['Risk', 'All']) {
  const rows = readFileSync(join(dir, `${preset}-rows.json`), 'utf8');
  const columnar = readFileSync(join(dir, `${preset}-columnar.json`), 'utf8');
  const msgpack = readFileSync(join(dir, `${preset}-columnar.msgpack`));
  console.log(`\n${preset} preset, 200 rows`);
  console.log('| Format                       | parse p50 | +rows p50 | +rows p95 |');
  console.log('|------------------------------|-------:|-------:|-------:|');
  bench('Row JSON', () => JSON.parse(rows), (d) => d.rows);
  bench('Columnar JSON (chosen)', () => JSON.parse(columnar), toRows);
  bench('Columnar MessagePack', () => decode(msgpack), toRows);
}
