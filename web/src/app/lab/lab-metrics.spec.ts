import { decode, encode } from '@msgpack/msgpack';
import {
  LabRun, LabSettings, acceptFor, cacheSummary, cells, decodeBlock, defaultSettings, encodingOf, keepRuns, labHeaders, maxRuns,
  moneyColumns, network, parseServerTiming, scenario, sum, toMarkdown,
} from './lab-metrics';

const bytes = (text: string): ArrayBuffer => new TextEncoder().encode(text).buffer as ArrayBuffer;

const run = (id: number, over: Partial<LabRun> = {}): LabRun => ({
  id, scenario: 'P1 · columnar JSON', requests: 1, rows: 200, columns: 42, wireBytes: 23_757, decodedBytes: 63_386, encoding: 'br',
  ttfbMs: 12.34, downloadMs: 1.2, parseMs: 0.8, renderMs: 15.1, totalMs: 14.9, server: { db: 8.2, ser: 0.4, total: 9.1 }, cache: 'MISS', ...over,
});

const entry = (over: Partial<PerformanceResourceTiming>): PerformanceResourceTiming => ({
  startTime: 100, requestStart: 102, responseStart: 120, responseEnd: 125, encodedBodySize: 2_000, decodedBodySize: 8_000, ...over,
}) as PerformanceResourceTiming;

describe('Performance Lab metrics (README §6 P5)', () => {
  it('asks for each payload format by its media type', () => {
    expect(acceptFor('rows')).toBe('application/vnd.desk.rows+json');
    expect(acceptFor('columnar')).toBe('application/json');
    expect(acceptFor('msgpack')).toBe('application/x-msgpack');
  });

  it('sends a switch header only for a toggle that is not at its default', () => {
    expect(labHeaders({ cacheOff: false, encoding: 'default' })).toEqual({});
    expect(labHeaders({ cacheOff: true, encoding: 'identity' })).toEqual({ 'X-Lab-Cache': 'off', 'X-Lab-Encoding': 'identity' });
  });

  it('reads the Server-Timing breakdown, or null when a part is missing', () => {
    expect(parseServerTiming('db;dur=38.0, ser;dur=6.1, total;dur=45.2')).toEqual({ db: 38, ser: 6.1, total: 45.2 });
    expect(parseServerTiming('db;dur=0.0, ser;dur=0.0, total;dur=0.3')).toEqual({ db: 0, ser: 0, total: 0.3 });
    expect(parseServerTiming('db;dur=1, total;dur=2')).toBeNull();
    expect(parseServerTiming(null)).toBeNull();
  });

  it('takes bytes and phases from the resource timing entry', () => {
    expect(network(entry({}))).toEqual({ wire: 2_000, decoded: 8_000, ttfb: 18, download: 5, start: 100, firstByte: 120, end: 125 });
    // No requestStart (a reused entry without detail): TTFB from the entry's start.
    expect(network(entry({ requestStart: 0 })).ttfb).toBe(20);
  });

  it('reports n/a, never 0, for an opaque cross-origin entry', () => {
    const n = network(entry({ requestStart: 0, responseStart: 0, encodedBodySize: 0, decodedBodySize: 0 }));
    expect(n).toMatchObject({ wire: null, decoded: null, ttfb: null, download: null, firstByte: null });
  });

  it('names the encoding from the echo, the header or the sizes', () => {
    expect(encodingOf('identity', null, null)).toBe('none');
    expect(encodingOf('gzip', 'gzip', null)).toBe('gzip');
    expect(encodingOf(null, 'br', null)).toBe('br');
    expect(encodingOf(null, null, { wire: 10, decoded: 40 })).toBe('compressed');
    expect(encodingOf(null, null, { wire: 40, decoded: 40 })).toBe('none');
    expect(encodingOf(null, null, { wire: null, decoded: null })).toBe('?');
  });

  describe('decodes every payload format to the same rows', () => {
    const columns = ['position_id', 'deal_name', 'market_value'];
    const data = [[1, 2], ['CLO 2024-3', 'RMBS 2021-1'], [1250000.25, null]];
    const expected = [
      { position_id: 1, deal_name: 'CLO 2024-3', market_value: 1250000.25 },
      { position_id: 2, deal_name: 'RMBS 2021-1', market_value: null },
    ];
    const money = new Set(['market_value']);

    it('row JSON', () => {
      expect(decodeBlock('rows', bytes(JSON.stringify({ rows: expected, rowCount: 2 })), money)).toEqual({ rows: expected, columns });
      expect(decodeBlock('rows', bytes('{"rows":[]}'), money)).toEqual({ rows: [], columns: [] });
    });

    it('columnar JSON', () => {
      expect(decodeBlock('columnar', bytes(JSON.stringify({ columns, data })), money)).toEqual({ rows: expected, columns });
    });

    it('MessagePack, with money strings parsed', () => {
      const packed = encode({ columns, data: [data[0], data[1], ['1250000.25', null]] });
      const buffer = packed.buffer.slice(packed.byteOffset, packed.byteOffset + packed.byteLength) as ArrayBuffer;
      expect(decodeBlock('msgpack', buffer, money, decode)).toEqual({ rows: expected, columns });
      expect(() => decodeBlock('msgpack', buffer, money)).toThrow(/not loaded/);
    });
  });

  it('finds the money columns in the catalog', () => {
    expect(moneyColumns([
      { name: 'market_value', group: 'g', kind: 'Money', aggregation: 'Sum', header: 'MV' },
      { name: 'dv01', group: 'g', kind: 'Ratio', aggregation: 'Sum', header: 'DV01' },
    ])).toEqual(new Set(['market_value']));
  });

  it('keeps the last 20 runs, newest first', () => {
    let runs: LabRun[] = [];
    for (let i = 1; i <= 25; i++) runs = keepRuns(runs, run(i));
    expect(runs).toHaveLength(maxRuns);
    expect(runs[0].id).toBe(25);
    expect(runs.at(-1)!.id).toBe(6);
  });

  it('describes a run by the toggles that applied to it', () => {
    const s: LabSettings = { ...defaultSettings, format: 'msgpack', blockSize: 500, columns: 'all', cacheOff: true, encoding: 'identity' };
    expect(scenario(s, 197)).toBe('P1 · MessagePack · 500 rows · all 197 cols · cache off · no compression');
    expect(scenario({ ...defaultSettings, format: 'rows' }, 42)).toBe('P1 · row JSON · 200 rows · displayed 42 cols · cache on');
    expect(scenario({ ...defaultSettings, encoding: 'gzip' }, 42)).toBe('P1 · columnar JSON · 200 rows · displayed 42 cols · cache on · gzip');
    expect(scenario({ ...defaultSettings, target: 'p3', p3: 'naive' }, 0)).toBe('P3 · naive 20 calls · cache on');
    expect(scenario({ ...defaultSettings, target: 'p3' }, 0)).toBe('P3 · 5 per-source calls · cache on');
  });

  it('summarises the cache status of several responses', () => {
    expect(cacheSummary(['MISS'])).toBe('MISS');
    expect(cacheSummary(['HIT', 'MISS', 'HIT', null])).toBe('2 HIT · 1 MISS · 1 ?');
    expect(cacheSummary(['BYPASS', 'BYPASS'])).toBe('2 BYPASS');
  });

  it('sums only the known values', () => {
    expect(sum([1, null, 2.5])).toBe(3.5);
    expect(sum([null])).toBeNull();
  });

  it('formats a run, with n/a for what was not measured', () => {
    expect(cells(run(1))).toEqual(['1', 'P1 · columnar JSON', '1', '200 × 42', '23.2', '61.9', 'br', '12.3', '1.2', '0.8', '15.1', '14.9', '8.2 / 0.4 / 9.1', 'MISS']);
    expect(cells(run(2, { rows: null, columns: null, wireBytes: null, decodedBytes: null, ttfbMs: null, server: null }))).toEqual(
      ['2', 'P1 · columnar JSON', '1', 'n/a', 'n/a', 'n/a', 'br', 'n/a', '1.2', '0.8', '15.1', '14.9', 'n/a', 'MISS']);
    expect(cells(run(3, { columns: null }))[3]).toBe('200 × n/a');
  });

  it('copies as a markdown table, oldest run first, with pipes escaped', () => {
    const md = toMarkdown([run(2, { scenario: 'a | b' }), run(1)]);
    const lines = md.trimEnd().split('\n');
    expect(lines[0]).toBe('| # | Scenario | Req | Rows × cols | Wire KB | Decoded KB | Encoding | TTFB ms | Download ms | Parse ms | Render ms | Total ms | Server db / ser / total ms | Cache |');
    expect(lines[1]).toBe('|---:|---|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|');
    expect(lines[2].startsWith('| 1 | P1 · columnar JSON |')).toBe(true);
    expect(lines[3]).toContain('| 2 | a \\| b |');
    expect(md.endsWith('\n')).toBe(true);
  });
});
