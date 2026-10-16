import { Component, input, output, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { AgGridAngular } from 'ag-grid-angular';
import { encode } from '@msgpack/msgpack';
import { AuthService } from '../core/auth.service';
import { ScopeService } from '../core/scope.service';
import { CatalogColumn, Preset } from '../data-access/api.types';
import { insightSources } from '../insights/source-stream';
import { LabClient, ResourceTimings, tagOf } from './lab-client';
import { PerformanceLab, afterPaint, loadMsgPack, timed } from './performance-lab';

/** Stands in for <ag-grid-angular> in jsdom: same inputs, no layout engine. */
// eslint-disable-next-line @angular-eslint/component-selector -- must match the real grid's selector to stand in for it
@Component({ selector: 'ag-grid-angular', template: '' })
class StubGrid {
  readonly gridOptions = input<unknown>();
  readonly gridReady = output<unknown>();
}

const catalog: CatalogColumn[] = [
  { name: 'position_id', group: 'Keys', kind: 'Key', aggregation: 'None', header: 'Position' },
  { name: 'deal_name', group: 'Keys', kind: 'Text', aggregation: 'None', header: 'Deal' },
  { name: 'market_value', group: 'Risk', kind: 'Money', aggregation: 'Sum', header: 'MV' },
  { name: 'dv01', group: 'Risk', kind: 'Ratio', aggregation: 'Sum', header: 'DV01' },
];
const presets: Preset[] = [
  { name: 'Risk', builtIn: true, state: { columns: ['deal_name', 'market_value'] }, updatedAt: null },
  { name: 'Risk', builtIn: false, state: { columns: ['dv01'] }, updatedAt: 'x' },
];

/** Fake resource timing: every tagged request gets an entry with these sizes and phases. */
class FakeTimings {
  sizes = { encodedBodySize: 2_048, decodedBodySize: 8_192 };
  readonly entry = vi.fn(async (tag: string) => ({
    name: `/x?lab=${tag}`, startTime: 1_000, requestStart: 1_002, responseStart: 1_020, responseEnd: 1_025, ...this.sizes,
  }) as unknown as PerformanceResourceTiming);
}

const columnar = (columns: string[], rows: number) => ({
  columns: ['position_id', ...columns],
  data: [[...Array(rows).keys()], ...columns.map((c) => Array.from({ length: rows }, (_, i) => (c === 'deal_name' ? `Deal ${i}` : i * 1.5)))],
  rowCount: 1234, summary: {}, asOf: '2026-10-06', generatedAt: '2026-10-07T00:00:00Z',
});
const buffer = (u: Uint8Array) => u.buffer.slice(u.byteOffset, u.byteOffset + u.byteLength) as ArrayBuffer;
const json = (doc: unknown) => buffer(new TextEncoder().encode(JSON.stringify(doc)));
const timing = { 'Server-Timing': 'db;dur=8.2, ser;dur=0.4, total;dur=9.1' };

async function until<T>(fn: () => T | undefined | null | false, ms = 2000): Promise<T> {
  const end = Date.now() + ms;
  for (;;) {
    const v = fn();
    if (v) return v;
    if (Date.now() > end) throw new Error('timed out waiting');
    await new Promise((r) => setTimeout(r, 5));
  }
}

describe('Performance Lab (README §6 P5)', () => {
  afterEach(() => vi.restoreAllMocks());

  interface Options { admin?: boolean; grid?: boolean; asOfFails?: boolean; presets?: Preset[] | 'fail'; }

  async function render(adminOrOptions: boolean | Options = false) {
    const o: Options = typeof adminOrOptions === 'boolean' ? { admin: adminOrOptions } : adminOrOptions;
    const admin = o.admin ?? false;
    const timings = new FakeTimings();
    TestBed.configureTestingModule({
      imports: [PerformanceLab],
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: AuthService, useValue: { isAdmin: signal(admin) } }],
    });
    TestBed.overrideComponent(PerformanceLab, {
      remove: { imports: [AgGridAngular], providers: [ResourceTimings, LabClient] },
      add: { imports: [StubGrid], providers: [{ provide: ResourceTimings, useValue: timings }, LabClient] },
    });
    const fixture = TestBed.createComponent(PerformanceLab);
    const http = TestBed.inject(HttpTestingController);
    const el = fixture.nativeElement as HTMLElement;
    const grid = { setGridOption: vi.fn() };
    if (o.grid !== false)
      (fixture.componentInstance as unknown as { gridOptions: { onGridReady: (e: unknown) => void } }).gridOptions.onGridReady({ api: grid });
    await fixture.whenStable();
    http.expectOne('/api/meta/columns').flush(catalog);
    if (o.presets === 'fail') http.expectOne('/api/presets/positions').flush(null, { status: 500, statusText: 'Error' });
    else http.expectOne('/api/presets/positions').flush(o.presets ?? presets);
    // The scope loads as-of and portfolios together (forkJoin): a failed as-of cancels the portfolios request.
    if (o.asOfFails) http.expectOne('/api/meta/as-of').flush(null, { status: 500, statusText: 'Error' });
    else http.expectOne('/api/meta/as-of').flush({ latest: '2026-10-06', dates: ['2026-10-06'] });
    if (!o.asOfFails) http.expectOne('/api/meta/portfolios').flush([{ portfolioId: 3, name: 'Book 03', fundId: 1, fundName: 'Fund' }]);
    await fixture.whenStable();
    const button = (id: string) => el.querySelector<HTMLButtonElement>(`[data-testid=${id}]`)!;
    const click = async (id: string) => { button(id).click(); await fixture.whenStable(); };
    const positions = () => until(() => http.match((r) => r.url.startsWith('/api/positions/query?lab='))[0]);
    const row = (id: number) => [...el.querySelectorAll(`[data-testid=run-${id}] td`)].map((td) => td.textContent?.trim());
    const settled = () => until(() => !fixture.componentInstance['running']() && fixture.whenStable().then(() => true));
    const insights = () => until(() => { const m = http.match((r) => r.url.startsWith('/api/insights/')); return m.length ? m : null; });
    return { fixture, http, el, grid, timings, button, click, positions, insights, row, settled };
  }

  it('runs one columnar block with the displayed columns and fills a row from the real response', async () => {
    const { http, el, grid, click, positions, row, settled, fixture } = await render();
    expect(el.querySelector('[data-testid=results] .empty')).toBeTruthy();
    expect(el.querySelector('[data-testid=column-count]')?.textContent).toContain('2 requested');
    await click('run');
    const req = await positions();
    expect(req.request.body).toEqual({ asOf: '2026-10-06', portfolioIds: [], startRow: 0, endRow: 200, columns: ['deal_name', 'market_value'] });
    expect(req.request.headers.get('Accept')).toBe('application/json');
    expect(req.request.headers.has('X-Lab-Cache')).toBe(false);
    expect(req.request.headers.has('X-Lab-Encoding')).toBe(false);
    expect(el.querySelector<HTMLButtonElement>('[data-testid=run]')!.disabled).toBe(true);
    req.flush(json(columnar(['deal_name', 'market_value'], 200)), { headers: { ...timing, 'X-Cache': 'MISS', 'Content-Encoding': 'br' } });
    await settled();
    await fixture.whenStable();

    expect(row(1)).toEqual(['1', 'P1 · columnar JSON · 200 rows · displayed 2 cols · cache on', '1', '200 × 3', '2.0', '8.0', 'br',
      '18.0', '5.0', expect.stringMatching(/^\d+\.\d$/), expect.stringMatching(/^\d+\.\d$/), '25.0', '8.2 / 0.4 / 9.1', 'MISS']);
    const rows = grid.setGridOption.mock.calls.find(([k]) => k === 'rowData')![1] as Record<string, unknown>[];
    expect(rows).toHaveLength(200);
    expect(rows[1]).toEqual({ position_id: 1, deal_name: 'Deal 1', market_value: 1.5 });
    const defs = grid.setGridOption.mock.calls.find(([k]) => k === 'columnDefs')![1] as { field: string; headerName: string; valueFormatter?: (p: { value: unknown }) => string }[];
    expect(defs.map((d) => d.headerName)).toEqual(['Position', 'Deal', 'MV']);
    expect(defs[2].valueFormatter!({ value: 1250000.4 })).toBe('1,250,000');
    expect(http.match(() => true)).toHaveLength(0);
  });

  it('applies every P1 toggle: MessagePack, 500 rows, all columns, cache off (admin) and a forced encoding', async () => {
    const { el, click, positions, row, settled, fixture } = await render(true);
    for (const id of ['format-msgpack', 'block-500', 'columns-all', 'cache-off', 'encoding-gzip']) await click(id);
    expect(el.querySelector('[data-testid=format-msgpack]')!.getAttribute('aria-pressed')).toBe('true');
    expect(el.querySelector('[data-testid=column-count]')?.textContent).toContain('4 requested');
    await click('run');
    const req = await positions();
    expect(req.request.body.endRow).toBe(500);
    expect(req.request.body.columns).toEqual(['position_id', 'deal_name', 'market_value', 'dv01']);
    expect(req.request.headers.get('Accept')).toBe('application/x-msgpack');
    expect(req.request.headers.get('X-Lab-Cache')).toBe('off');
    expect(req.request.headers.get('X-Lab-Encoding')).toBe('gzip');
    const doc = columnar(['deal_name', 'market_value', 'dv01'], 500);
    doc.data[2] = doc.data[2].map(String); // money as exact strings
    req.flush(buffer(encode(doc)), { headers: { ...timing, 'X-Cache': 'BYPASS', 'X-Lab-Encoding': 'gzip' } });
    await settled();
    await fixture.whenStable();
    const cellsOf = row(1);
    expect(cellsOf[1]).toBe('P1 · MessagePack · 500 rows · all 4 cols · cache off · gzip');
    expect(cellsOf[3]).toBe('500 × 4');
    expect(cellsOf[6]).toBe('gzip');
    expect(cellsOf[13]).toBe('BYPASS');
  });

  it('decodes row JSON, and says n/a where the browser reported nothing', async () => {
    const { click, positions, row, settled, fixture, timings } = await render();
    timings.entry.mockResolvedValueOnce(null as unknown as PerformanceResourceTiming);
    await click('format-rows');
    await click('run');
    const req = await positions();
    expect(req.request.headers.get('Accept')).toBe('application/vnd.desk.rows+json');
    req.flush(json({ rows: [{ position_id: 1, deal_name: 'A', market_value: 2 }], rowCount: 1 }), { headers: { 'X-Lab-Encoding': 'identity' } });
    await settled();
    await fixture.whenStable();
    expect(row(1)).toEqual(['1', 'P1 · row JSON · 200 rows · displayed 2 cols · cache on', '1', '1 × 3', 'n/a', 'n/a', 'none', 'n/a', 'n/a',
      expect.any(String), expect.any(String), 'n/a', 'n/a', '?']);
  });

  it('keeps the cache toggle for admins: a viewer cannot switch it off', async () => {
    const { button } = await render(false);
    expect(button('cache-off').disabled).toBe(true);
    expect(button('cache-off').title).toBe('Admin only');
  });

  it('runs the P3 board both ways: 5 per-source calls, then 20 naive ones, and sums what they cost', async () => {
    const { http, click, row, settled, fixture, el } = await render();
    await click('target-p3');
    expect(el.querySelector('[data-testid=format-rows]')).toBeNull();
    await click('run');
    const five = await until(() => { const m = http.match((r) => r.url.startsWith('/api/insights/')); return m.length ? m : null; });
    expect(five.map((r) => r.request.url.split('?')[0])).toEqual(insightSources.map((s) => `/api/insights/${s.source}`));
    expect(five[0].request.url).toContain('asOf=2026-10-06');
    five.forEach((r, i) => r.flush(json({ grids: [] }), { headers: { ...timing, 'X-Cache': i < 3 ? 'HIT' : 'MISS' } }));
    await settled();
    await fixture.whenStable();
    expect(row(1)).toEqual(['1', 'P3 · 5 per-source calls · cache on', '5', 'n/a', '10.0', '40.0', 'compressed', expect.any(String), 'n/a',
      expect.any(String), 'n/a', expect.any(String), '41.0 / 2.0 / 45.5', '3 HIT · 2 MISS']);

    await click('p3-naive');
    await click('run');
    const twenty: TestRequest[] = await until(() => { const m = http.match((r) => r.url.startsWith('/api/insights/')); return m.length ? m : null; });
    expect(twenty).toHaveLength(20);
    expect(twenty.every((r) => /[?&]grid=\w+/.test(r.request.url))).toBe(true);
    twenty.forEach((r) => r.flush(json({ grids: [] }), { headers: { 'X-Cache': 'HIT' } }));
    await settled();
    await fixture.whenStable();
    expect(row(2).slice(1, 3)).toEqual(['P3 · naive 20 calls · cache on', '20']);
    expect(row(2)[12]).toBe('n/a'); // no Server-Timing on these responses
    expect(row(2)[13]).toBe('20 HIT');
    expect(el.querySelectorAll('[data-testid=results] tbody tr')[0].getAttribute('data-testid')).toBe('run-2');
  });

  it('shows a failed run and keeps the table as it was', async () => {
    const { click, positions, settled, fixture, el } = await render();
    await click('run');
    (await positions()).flush(null, { status: 503, statusText: 'Unavailable' });
    await settled();
    await fixture.whenStable();
    expect(el.querySelector('[data-testid=error]')?.textContent).toContain('HTTP 503');
    expect(el.querySelector('[data-testid=results] .empty')).toBeTruthy();
  });

  it('copies the runs as markdown, falls back to a text box, and clears', async () => {
    const { click, positions, settled, fixture, el, button } = await render();
    expect(button('copy').disabled).toBe(true);
    await click('run');
    (await positions()).flush(json(columnar(['deal_name', 'market_value'], 3)), { headers: timing });
    await settled();
    await fixture.whenStable();

    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    await click('copy');
    await until(() => el.querySelector('[data-testid=copied]'));
    expect(el.querySelector('[data-testid=copied]')?.textContent).toBe('Copied 1 run as markdown.');
    const md = writeText.mock.calls[0][0] as string;
    expect(md.split('\n')[0]).toMatch(/^\| # \| Scenario \|/);
    expect(md).toContain('| 1 | P1 · columnar JSON · 200 rows · displayed 2 cols · cache on | 1 | 3 × 3 |');

    writeText.mockRejectedValueOnce(new Error('denied'));
    await click('copy');
    const box = await until(() => el.querySelector<HTMLTextAreaElement>('[data-testid=markdown]'));
    expect(box.value).toBe(md);
    expect(el.querySelector('[data-testid=copied]')).toBeNull();

    await click('clear');
    expect(el.querySelector('[data-testid=markdown]')).toBeNull();
    expect(el.querySelector('[data-testid=results] .empty')).toBeTruthy();
  });

  it('runs without an as-of when the scope failed to load, and sends the selected portfolios', async () => {
    const { click, positions, insights, settled, fixture } = await render({ asOfFails: true });
    TestBed.inject(ScopeService).selected.set([3]);
    await click('run');
    const req = await positions();
    expect(req.request.body.asOf).toBeUndefined();
    expect(req.request.body.portfolioIds).toEqual([3]);
    req.flush(json(columnar(['deal_name', 'market_value'], 1)));
    await settled();

    TestBed.inject(ScopeService).selected.set([]);
    await click('target-p3');
    await click('run');
    const calls = await insights();
    expect(calls[0].request.url).toMatch(/^\/api\/insights\/core\?lab=/);
    calls.forEach((r) => r.flush(json({ grids: [] })));
    await settled();
    await fixture.whenStable();
  });

  it('reports n/a for P3 sums when the browser misses an entry, or hides the first byte', async () => {
    const { click, insights, row, settled, fixture, timings } = await render();
    TestBed.inject(ScopeService).selected.set([3, 7]);
    await click('target-p3');
    timings.entry.mockResolvedValueOnce(null as unknown as PerformanceResourceTiming);
    await click('run');
    const calls = await insights();
    expect(calls[0].request.url).toContain('portfolioIds=3%2C7');
    calls.forEach((r) => r.flush(json({ grids: [] }), { headers: timing }));
    await settled();
    await fixture.whenStable();
    expect(row(1).slice(4, 12)).toEqual(['n/a', 'n/a', '? / compressed', 'n/a', 'n/a', expect.any(String), 'n/a', 'n/a']);

    // An opaque entry (no Timing-Allow-Origin): sizes and the first byte unknown, the end still known.
    timings.sizes = { encodedBodySize: 0, decodedBodySize: 0 };
    timings.entry.mockImplementation(async (tag: string) => ({
      name: `/x?lab=${tag}`, startTime: 1_000, requestStart: 0, responseStart: 0, responseEnd: 1_030, encodedBodySize: 0, decodedBodySize: 0,
    }) as unknown as PerformanceResourceTiming);
    await click('run');
    (await insights()).forEach((r) => r.flush(json({ grids: [] })));
    await settled();
    await fixture.whenStable();
    expect(row(2)[7]).toBe('n/a');
    expect(row(2)[11]).not.toBe('n/a');
  });

  it('labels a requested column the catalog does not know by its name, and skips the render without a grid', async () => {
    const { click, positions, row, settled, fixture } = await render({
      grid: false, presets: [{ name: 'Risk', builtIn: true, state: { columns: ['ghost'] }, updatedAt: null }],
    });
    await click('run');
    (await positions()).flush(json(columnar(['ghost'], 2)));
    await settled();
    await fixture.whenStable();
    expect(row(1)[10]).toBe('n/a'); // no grid, no render time
    const lab = fixture.componentInstance as unknown as { colDefs: (c: string[]) => { field: string; headerName: string }[] };
    expect(lab.colDefs(['ghost'])).toEqual([{ field: 'ghost', headerName: 'ghost' }]);
  });

  it.each([[[] as Preset[]], ['fail' as const]])('does not run without the displayed columns (presets %#)', async (p) => {
    const { fixture, http, button } = await render({ presets: p });
    expect(button('run').disabled).toBe(true);
    await (fixture.componentInstance as unknown as { run: () => Promise<void> }).run();
    expect(http.match((r) => r.url.startsWith('/api/positions/'))).toHaveLength(0);
  });

  it('pluralises the copy message', async () => {
    const { click, positions, settled, fixture, el } = await render();
    for (let i = 0; i < 2; i++) {
      await click('run');
      (await positions()).flush(json(columnar(['deal_name'], 1)));
      await settled();
      await fixture.whenStable();
    }
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: vi.fn().mockResolvedValue(undefined) }, configurable: true });
    await click('copy');
    expect((await until(() => el.querySelector('[data-testid=copied]'))).textContent).toBe('Copied 2 runs as markdown.');
  });

  it('says when the API could not be reached, and passes an error message through', async () => {
    const { click, positions, settled, fixture, el } = await render();
    await click('run');
    (await positions()).error(new ProgressEvent('error'));
    await settled();
    await fixture.whenStable();
    expect(el.querySelector('[data-testid=error]')?.textContent).toContain('could not be reached');

    const lab = fixture.componentInstance as unknown as { runP1: () => Promise<never> };
    vi.spyOn(lab, 'runP1').mockRejectedValueOnce(new Error('decode failed'));
    await click('run');
    await until(() => el.querySelector('[data-testid=error]')?.textContent?.includes('decode failed'));
  });

  it('describes a run that fails before the request', async () => {
    const { click, fixture, el } = await render();
    const lab = fixture.componentInstance as unknown as { runP1: () => Promise<never> };
    vi.spyOn(lab, 'runP1').mockRejectedValueOnce('boom');
    await click('run');
    await until(() => el.querySelector('[data-testid=error]'));
    expect(el.querySelector('[data-testid=error]')?.textContent).toContain('Run failed: boom');
  });
});

describe('Performance Lab helpers', () => {
  it('times a function between performance marks', () => {
    const { value, ms } = timed('lab:test', () => 42);
    expect(value).toBe(42);
    expect(ms).toBeGreaterThanOrEqual(0);
    expect(performance.getEntriesByName('lab:test', 'measure')).toHaveLength(1);
  });

  it('waits for the next painted frame, with or without requestAnimationFrame', async () => {
    await afterPaint();
    const raf = globalThis.requestAnimationFrame;
    vi.stubGlobal('requestAnimationFrame', undefined);
    await afterPaint();
    vi.stubGlobal('requestAnimationFrame', raf);
    vi.unstubAllGlobals();
  });

  it('loads the MessagePack decoder once', async () => {
    const first = loadMsgPack();
    expect(loadMsgPack()).toBe(first);
    expect(typeof (await first)).toBe('function');
  });

  it('reads tags from absolute URLs too', () => {
    expect(tagOf('http://localhost:8080/api/positions/query?lab=k-9')).toBe('k-9');
  });
});
