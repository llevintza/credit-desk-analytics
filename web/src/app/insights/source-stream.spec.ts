import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Subject } from 'rxjs';
import { InsightGrid, InsightsResult } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';
import { BoardQuery, SourceState, debounceMs, insightSources, sourceStream } from './source-stream';

const grid = (id: string): InsightGrid => ({ id, title: id, columns: ['Label', 'MV'], rows: [['CLO', 1]], format: { Label: 'text', MV: 'money0' } });
const result = (ids: string[]): InsightsResult => ({ source: 'market', asOf: '2026-10-06', grids: ids.map(grid) });
const q = (over: Partial<BoardQuery> = {}): BoardQuery => ({ asOf: '2026-10-06', portfolioIds: [], naive: false, retry: 0, ...over });

describe('sourceStream (README §6 P3)', () => {
  let http: HttpTestingController;
  let api: DeskApi;
  let query$: Subject<BoardQuery | null>;
  let states: SourceState[];
  const market = insightSources.find((s) => s.source === 'market')!;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    api = TestBed.inject(DeskApi);
    query$ = new Subject();
    states = [];
    sourceStream(api, 'market', market.grids, query$).subscribe((s) => states.push(s));
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  it('lists 20 grids across five sources, in the API order', () => {
    expect(insightSources.map((s) => s.source)).toEqual(['core', 'market', 'surveillance', 'pricing', 'reference']);
    expect(insightSources.reduce((n, s) => n + s.grids.length, 0)).toBe(20);
  });

  it('debounces the inputs, then shows loading until the response paints', () => {
    query$.next(q({ portfolioIds: [3, 7] }));
    http.expectNone(() => true);
    vi.advanceTimersByTime(debounceMs);
    const req = http.expectOne((r) => r.url === '/api/insights/market');
    expect(req.request.params.get('asOf')).toBe('2026-10-06');
    expect(req.request.params.get('portfolioIds')).toBe('3,7');
    expect(req.request.params.has('grid')).toBe(false);
    expect(states).toEqual([{ kind: 'loading' }]);
    req.flush(result(['spread_change_1m', 'curve_moves', 'spread_percentile_2y']));
    expect(states.at(-1)).toEqual({ kind: 'ready', grids: result(['spread_change_1m', 'curve_moves', 'spread_percentile_2y']).grids });
  });

  it('a new as-of cancels the request in flight (switchMap): only the latest one paints', () => {
    query$.next(q());
    vi.advanceTimersByTime(debounceMs);
    const first = http.expectOne((r) => r.url === '/api/insights/market');
    query$.next(q({ asOf: '2026-10-05' }));
    vi.advanceTimersByTime(debounceMs);
    expect(first.cancelled).toBe(true);
    const second = http.expectOne((r) => r.params.get('asOf') === '2026-10-05');
    second.flush(result(['curve_moves']));
    expect(states.filter((s) => s.kind === 'ready')).toHaveLength(1);
  });

  it('collapses a burst of inputs and identical repeats into one request', () => {
    query$.next(q({ asOf: 'a' }));
    query$.next(q({ asOf: 'b' }));
    query$.next(q());
    vi.advanceTimersByTime(debounceMs);
    http.expectOne((r) => r.url === '/api/insights/market').flush(result(['curve_moves']));
    query$.next(q());
    vi.advanceTimersByTime(debounceMs);
    http.expectNone(() => true);
  });

  it('a failing source becomes an error state, and a retry asks again', () => {
    query$.next(q());
    vi.advanceTimersByTime(debounceMs);
    http.expectOne((r) => r.url === '/api/insights/market').flush('boom', { status: 500, statusText: 'Server Error' });
    expect(states.at(-1)).toEqual({ kind: 'error' });
    query$.next(q({ retry: 1 }));
    vi.advanceTimersByTime(debounceMs);
    expect(states.at(-1)).toEqual({ kind: 'loading' });
    http.expectOne((r) => r.url === '/api/insights/market').flush(result(['curve_moves']));
    expect(states.at(-1)?.kind).toBe('ready');
  });

  it('no scope yet means loading and no request', () => {
    query$.next(null);
    vi.advanceTimersByTime(debounceMs);
    http.expectNone(() => true);
    expect(states).toEqual([{ kind: 'loading' }]);
  });

  it('naive mode fires one request per grid and paints each as it lands', () => {
    query$.next(q({ naive: true }));
    vi.advanceTimersByTime(debounceMs);
    const reqs = http.match((r) => r.url === '/api/insights/market');
    expect(reqs.map((r) => r.request.params.get('grid'))).toEqual([...market.grids]);
    reqs[1].flush(result(['curve_moves']));
    expect(states.at(-1)).toEqual({ kind: 'ready', grids: [null, grid('curve_moves'), null] });
    reqs[0].flush(result(['spread_change_1m']));
    reqs[2].flush({ ...result([]), grids: [] }); // a grid missing from its response stays empty, never mis-placed
    expect(states.at(-1)).toEqual({ kind: 'ready', grids: [grid('spread_change_1m'), grid('curve_moves'), null] });
  });
});
