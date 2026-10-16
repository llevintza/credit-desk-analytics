import { CatalogColumn, GridBlock } from '../data-access/api.types';
import { Row, toRows } from '../data-access/to-rows';

/** The Performance Lab's toggles (README §6 P5). P1 runs one positions block; P3 runs the insights board's reads. */
export type Target = 'p1' | 'p3';
export type PayloadFormat = 'rows' | 'columnar' | 'msgpack';
export type BlockSize = 100 | 200 | 500;
export type ColumnSet = 'displayed' | 'all';
/** `default` sends the browser's own Accept-Encoding; the others are forced by the server's dev knob (ADR-0028). */
export type Encoding = 'default' | 'br' | 'gzip' | 'identity';
export type P3Mode = 'per-source' | 'naive';

export interface LabSettings {
  target: Target;
  format: PayloadFormat;
  blockSize: BlockSize;
  columns: ColumnSet;
  cacheOff: boolean;
  encoding: Encoding;
  p3: P3Mode;
}

export const defaultSettings: LabSettings = {
  target: 'p1', format: 'columnar', blockSize: 200, columns: 'displayed', cacheOff: false, encoding: 'default', p3: 'per-source',
};

/** The request headers of the lab switches (src/Desk.Api/Lab/LabSwitches.cs). */
export const cacheHeader = 'X-Lab-Cache';
export const encodingHeader = 'X-Lab-Encoding';

const accepts: Record<PayloadFormat, string> = {
  rows: 'application/vnd.desk.rows+json',
  columnar: 'application/json',
  msgpack: 'application/x-msgpack',
};

export const acceptFor = (format: PayloadFormat): string => accepts[format];

/** The switch headers a run sends. The server ignores the cache one for viewers and the encoding one outside dev. */
export function labHeaders(s: Pick<LabSettings, 'cacheOff' | 'encoding'>): Record<string, string> {
  const headers: Record<string, string> = {};
  if (s.cacheOff) headers[cacheHeader] = 'off';
  if (s.encoding !== 'default') headers[encodingHeader] = s.encoding;
  return headers;
}

export interface ServerTiming { db: number; ser: number; total: number; }

/** `db;dur=38.0, ser;dur=6.1, total;dur=45.2` (README §8). Null when absent or incomplete. */
export function parseServerTiming(header: string | null): ServerTiming | null {
  if (!header) return null;
  const dur = (name: string): number | null => {
    const m = new RegExp(`(?:^|,)\\s*${name};dur=([\\d.]+)`).exec(header);
    return m ? Number(m[1]) : null;
  };
  const db = dur('db'), ser = dur('ser'), total = dur('total');
  return db === null || ser === null || total === null ? null : { db, ser, total };
}

/**
 * What Resource Timing says about one response (README §6 P5). `ttfb` and `download` are durations; `start`,
 * `firstByte` and `end` are page times, for runs of several requests.
 */
export interface Network {
  wire: number | null; decoded: number | null; ttfb: number | null; download: number | null;
  start: number; firstByte: number | null; end: number;
}

/**
 * Bytes and times from a resource timing entry. A cross-origin response without Timing-Allow-Origin reports zero
 * sizes and request times: those become null rather than a misleading 0. TTFB runs from the request being sent
 * (so it excludes a queue wait for a connection) to the first byte; download from the first byte to the last.
 */
export function network(e: PerformanceResourceTiming): Network {
  const opaque = e.responseStart === 0;
  const sent = e.requestStart > 0 ? e.requestStart : e.startTime;
  return {
    wire: opaque || e.decodedBodySize === 0 ? null : e.encodedBodySize,
    decoded: opaque || e.decodedBodySize === 0 ? null : e.decodedBodySize,
    ttfb: opaque ? null : e.responseStart - sent,
    download: opaque ? null : e.responseEnd - e.responseStart,
    start: e.startTime,
    firstByte: opaque ? null : e.responseStart,
    end: e.responseEnd,
  };
}

/**
 * The encoding a response came with: the forced one the server echoed, else Content-Encoding (same origin), else
 * "none" when the sizes match. "?" when nothing says.
 */
export function encodingOf(echoed: string | null, contentEncoding: string | null, net: Pick<Network, 'wire' | 'decoded'> | null): string {
  if (echoed) return echoed === 'identity' ? 'none' : echoed;
  if (contentEncoding) return contentEncoding;
  if (net?.wire != null && net.decoded != null) return net.wire < net.decoded ? 'compressed' : 'none';
  return '?';
}

/** The MessagePack decoder, loaded on first use so it stays out of every other chunk (ADR-0028). */
export type MsgPackDecode = (bytes: Uint8Array) => unknown;

/**
 * Bytes → grid rows, as the page would do it: row JSON is already rows; columnar JSON and MessagePack are transposed
 * (ADR-0007). MessagePack carries money as exact strings, so those columns become numbers here, part of its cost.
 */
export function decodeBlock(format: PayloadFormat, bytes: ArrayBuffer, money: ReadonlySet<string>, msgpack?: MsgPackDecode): { rows: Row[]; columns: string[] } {
  if (format === 'msgpack') {
    if (!msgpack) throw new Error('The MessagePack decoder is not loaded.');
    const block = msgpack(new Uint8Array(bytes)) as Pick<GridBlock, 'columns' | 'data'>;
    const data = block.columns.map((c, i) => (money.has(c) ? block.data[i].map((v) => (v === null ? null : Number(v))) : block.data[i]));
    return { rows: toRows({ columns: block.columns, data }), columns: block.columns };
  }
  const doc: unknown = JSON.parse(new TextDecoder().decode(bytes));
  if (format === 'rows') {
    const rows = (doc as { rows: Row[] }).rows;
    return { rows, columns: rows.length ? Object.keys(rows[0]) : [] };
  }
  const block = doc as Pick<GridBlock, 'columns' | 'data'>;
  return { rows: toRows(block), columns: block.columns };
}

export const moneyColumns = (catalog: CatalogColumn[]): Set<string> => new Set(catalog.filter((c) => c.kind === 'Money').map((c) => c.name));

/** One row of the results table. Nulls show as "n/a": the browser didn't report it, or it doesn't apply. */
export interface LabRun {
  id: number;
  scenario: string;
  requests: number;
  rows: number | null;
  columns: number | null;
  wireBytes: number | null;
  decodedBytes: number | null;
  encoding: string;
  ttfbMs: number | null;
  downloadMs: number | null;
  parseMs: number | null;
  renderMs: number | null;
  totalMs: number | null;
  server: ServerTiming | null;
  cache: string;
}

export const maxRuns = 20;

/** Newest first, at most the last 20 (README §6 P5). */
export const keepRuns = (runs: readonly LabRun[], run: LabRun): LabRun[] => [run, ...runs].slice(0, maxRuns);

/** What a run measured, in words: the toggles that applied to it. */
export function scenario(s: LabSettings, columnCount: number): string {
  const cache = s.cacheOff ? 'cache off' : 'cache on';
  const enc = s.encoding === 'default' ? null : s.encoding === 'identity' ? 'no compression' : s.encoding;
  const parts = s.target === 'p1'
    ? ['P1', { rows: 'row JSON', columnar: 'columnar JSON', msgpack: 'MessagePack' }[s.format], `${s.blockSize} rows`,
      `${s.columns === 'all' ? 'all' : 'displayed'} ${columnCount} cols`, cache, enc]
    : ['P3', s.p3 === 'naive' ? 'naive 20 calls' : '5 per-source calls', cache, enc];
  return parts.filter(Boolean).join(' · ');
}

/** "3 HIT · 2 MISS" for several responses; the one status for one. */
export function cacheSummary(statuses: (string | null)[]): string {
  const counts = new Map<string, number>();
  for (const s of statuses) counts.set(s ?? '?', (counts.get(s ?? '?') ?? 0) + 1);
  if (counts.size === 1 && statuses.length === 1) return [...counts.keys()][0];
  return [...counts].map(([s, n]) => `${n} ${s}`).join(' · ');
}

/** Sum of the known values; null when none is known. */
export function sum(values: (number | null)[]): number | null {
  const known = values.filter((v): v is number => v !== null);
  return known.length ? known.reduce((a, b) => a + b, 0) : null;
}

const kb = (b: number | null) => (b === null ? 'n/a' : (b / 1024).toFixed(1));
const ms = (v: number | null) => (v === null ? 'n/a' : v.toFixed(1));

/** The results table's columns, on the page and in the markdown copy. */
export const columnHeaders = ['#', 'Scenario', 'Req', 'Rows × cols', 'Wire KB', 'Decoded KB', 'Encoding', 'TTFB ms', 'Download ms', 'Parse ms',
  'Render ms', 'Total ms', 'Server db / ser / total ms', 'Cache'];

/** One run's cells, in {@link columnHeaders} order: KB of 1024 bytes (README §10), ms to 0.1. */
export function cells(r: LabRun): string[] {
  return [
    String(r.id), r.scenario, String(r.requests),
    r.rows === null ? 'n/a' : `${r.rows} × ${r.columns ?? 'n/a'}`,
    kb(r.wireBytes), kb(r.decodedBytes), r.encoding,
    ms(r.ttfbMs), ms(r.downloadMs), ms(r.parseMs), ms(r.renderMs), ms(r.totalMs),
    r.server ? `${r.server.db.toFixed(1)} / ${r.server.ser.toFixed(1)} / ${r.server.total.toFixed(1)}` : 'n/a',
    r.cache,
  ];
}

/** The runs as a GitHub markdown table, oldest first, ready for a PR or an ADR (README §6 P5). */
export function toMarkdown(runs: readonly LabRun[]): string {
  const escape = (s: string) => s.replace(/\|/g, '\\|');
  const line = (c: string[]) => `| ${c.map(escape).join(' | ')} |`;
  const align = columnHeaders.map((_, i) => (i === 1 || i === 6 || i === 13 ? '---' : '---:'));
  return [line(columnHeaders), `|${align.join('|')}|`, ...[...runs].reverse().map((r) => line(cells(r)))].join('\n') + '\n';
}
