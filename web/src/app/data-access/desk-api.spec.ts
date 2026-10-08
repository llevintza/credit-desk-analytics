import { TestBed } from '@angular/core/testing';
import { HttpHeaders, HttpResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { DeskApi } from './desk-api';

describe('DeskApi', () => {
  let api: DeskApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(DeskApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('knows every URL so components never build one', async () => {
    const calls: [Promise<unknown>, string, string][] = [
      [firstValueFrom(api.health()), 'GET', '/health'],
      [firstValueFrom(api.me()), 'GET', '/api/me'],
      [firstValueFrom(api.asOf()), 'GET', '/api/meta/as-of'],
      [firstValueFrom(api.portfolios()), 'GET', '/api/meta/portfolios'],
      [firstValueFrom(api.presets('positions')), 'GET', '/api/presets/positions'],
      [firstValueFrom(api.savePreset('positions', 'Mine', { columns: ['dv01'] }), { defaultValue: null }), 'PUT', '/api/presets/positions'],
      [firstValueFrom(api.deletePreset('positions', 'Mine'), { defaultValue: null }), 'DELETE', '/api/presets/positions?name=Mine'],
      [firstValueFrom(api.logout(), { defaultValue: null }), 'POST', '/api/auth/logout'],
    ];
    for (const [, method, url] of calls) {
      const req = http.expectOne((r) => r.urlWithParams === url && r.method === method);
      expect(req.request.method).toBe(method);
      req.flush({});
    }
    await Promise.all(calls.map((c) => c[0]));
  });

  it('fetches the catalog once per session, and again after a failure', async () => {
    const first = firstValueFrom(api.columns());
    http.expectOne('/api/meta/columns').flush('boom', { status: 500, statusText: 'x' });
    await expect(first).rejects.toBeTruthy();

    const second = firstValueFrom(api.columns());
    http.expectOne('/api/meta/columns').flush([{ name: 'dv01' }]);
    expect(await second).toEqual([{ name: 'dv01' }]);
    expect(await firstValueFrom(api.columns())).toEqual([{ name: 'dv01' }]); // no new request
  });

  it('exports with the view and no paging, as a blob', async () => {
    const file = firstValueFrom(api.exportPositions({ columns: ['dv01'] }));
    const req = http.expectOne('/api/positions/export');
    expect(req.request.body).toEqual({ columns: ['dv01'], startRow: 0, endRow: 0 });
    expect(req.request.responseType).toBe('blob');
    req.flush(new Blob(['a,b']));
    expect(await file).toBeInstanceOf(Blob);
  });

  it('reads cache status and server time from the response headers', () => {
    const res = new HttpResponse({ headers: new HttpHeaders({ 'X-Cache': 'HIT', 'Server-Timing': 'db;dur=0.0, ser;dur=0.0, total;dur=0.4' }) });
    expect(DeskApi.info(res, 10)).toMatchObject({ cache: 'HIT', serverMs: 0.4 });
    expect(DeskApi.info(new HttpResponse({ headers: new HttpHeaders({ 'X-Cache': 'weird' }) }), 0))
      .toMatchObject({ cache: null, serverMs: null, bytes: null });
  });
});

/** Stands in for the browser's PerformanceObserver: entries are delivered by `deliver` or picked up by `takeRecords`. */
class FakeObserver {
  static last: FakeObserver;
  queued: PerformanceEntry[] = [];
  observed: PerformanceObserverInit | undefined;
  constructor(private readonly callback: (list: { getEntries(): PerformanceEntry[] }) => void) {
    FakeObserver.last = this;
  }
  observe(init: PerformanceObserverInit): void {
    this.observed = init;
  }
  takeRecords(): PerformanceEntry[] {
    const taken = this.queued;
    this.queued = [];
    return taken;
  }
  deliver(...entries: PerformanceEntry[]): void {
    this.callback({ getEntries: () => entries });
  }
}

describe('DeskApi positions bytes (resource timing)', () => {
  let api: DeskApi;
  let http: HttpTestingController;
  const later = () => performance.now() + 1_000_000; // a start time after any request in the test began
  const entry = (startTime: number, encodedBodySize: number, name = 'http://x/api/positions/query') =>
    ({ name, startTime, encodedBodySize }) as PerformanceResourceTiming;
  const block = { columns: [], data: [], rowCount: 0, summary: {}, asOf: 'x', generatedAt: 'x' };

  function setUp(observer: unknown) {
    vi.stubGlobal('PerformanceObserver', observer);
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(DeskApi);
    http = TestBed.inject(HttpTestingController);
  }

  afterEach(() => {
    http.verify();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('attributes this request\'s entry, exactly, and never clears the page-wide timing buffer', async () => {
    setUp(FakeObserver);
    const clear = vi.spyOn(performance, 'clearResourceTimings');
    expect(FakeObserver.last.observed).toEqual({ entryTypes: ['resource'] });
    FakeObserver.last.deliver(entry(0, 999), entry(later(), 1, 'http://x/api/meta/columns')); // stale; another URL
    const result = firstValueFrom(api.positions({ startRow: 0, endRow: 200, columns: [] }));
    FakeObserver.last.queued.push(entry(later(), 2048)); // recorded, not yet delivered
    http.expectOne('/api/positions/query').flush(block);
    const { info } = await result;
    expect(info.bytes).toBe(2048);
    expect(info).not.toHaveProperty('bytesApprox');
    expect(clear).not.toHaveBeenCalled();

    // A request with no entry of its own reports no bytes (the stale entry is never attributed).
    const next = firstValueFrom(api.positions({ startRow: 0, endRow: 200, columns: [] }));
    http.expectOne('/api/positions/query').flush(block);
    expect((await next).info.bytes).toBeNull();
  });

  it('marks the bytes approximate when parallel blocks overlap, and reports no bytes for an empty body', async () => {
    setUp(FakeObserver);
    const a = firstValueFrom(api.positions({ startRow: 0, endRow: 200, columns: [] }));
    const b = firstValueFrom(api.positions({ startRow: 200, endRow: 400, columns: [] }));
    const [reqA, reqB] = http.match('/api/positions/query');
    const t = later();
    FakeObserver.last.deliver(entry(t + 2, 4096), entry(t + 1, 2048));
    reqB.flush(block);
    reqA.flush(block);
    const [infoA, infoB] = [(await a).info, (await b).info];
    expect([infoA.bytesApprox, infoB.bytesApprox]).toEqual([true, true]);
    expect([infoA.bytes, infoB.bytes].sort()).toEqual([2048, 4096]); // each entry is claimed once

    const empty = firstValueFrom(api.positions({ startRow: 0, endRow: 200, columns: [] }));
    FakeObserver.last.queued.push(entry(later(), 0));
    http.expectOne('/api/positions/query').flush(block);
    expect((await empty).info).toMatchObject({ bytes: null });
  });

  it('reports no bytes where the browser has no PerformanceObserver', async () => {
    setUp(undefined);
    expect((api as unknown as { observer: unknown }).observer).toBeNull();
    const result = firstValueFrom(api.positions({ startRow: 0, endRow: 200, columns: [] }));
    http.expectOne('/api/positions/query').flush(block);
    expect((await result).info.bytes).toBeNull();
  });
});
