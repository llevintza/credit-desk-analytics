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

  it('reads cache status, server time and this request\'s compressed size from the response', () => {
    const entry = (startTime: number, encodedBodySize: number, name = 'http://x/api/positions/query') =>
      ({ name, startTime, encodedBodySize }) as PerformanceResourceTiming;
    const entries = vi.spyOn(performance, 'getEntriesByType').mockReturnValue([
      entry(5, 999),                                    // an earlier request
      entry(30, 4096),                                  // a parallel block started later
      entry(12, 2048),                                  // this one: first to start after `started`
      entry(20, 1, 'http://x/api/meta/columns'),
    ]);
    const res = new HttpResponse({ headers: new HttpHeaders({ 'X-Cache': 'HIT', 'Server-Timing': 'db;dur=0.0, ser;dur=0.0, total;dur=0.4' }) });
    expect(DeskApi.info(res, 10)).toMatchObject({ cache: 'HIT', serverMs: 0.4, bytes: 2048 });

    entries.mockReturnValue([]);
    expect(DeskApi.info(new HttpResponse({ headers: new HttpHeaders({ 'X-Cache': 'weird' }) }), 0))
      .toMatchObject({ cache: null, serverMs: null, bytes: null });
    entries.mockReturnValue([entry(1, 0)]);
    expect(DeskApi.info(new HttpResponse(), 0).bytes).toBeNull();

    // Near the browser's 250-entry limit the buffer is cleared so later requests are still recorded.
    const clear = vi.spyOn(performance, 'clearResourceTimings');
    entries.mockReturnValue(Array.from({ length: 201 }, (_, i) => entry(i, 10)));
    DeskApi.info(new HttpResponse(), 0);
    expect(clear).toHaveBeenCalled();
    entries.mockRestore();
    clear.mockRestore();

    const original = performance.getEntriesByType;
    Object.defineProperty(performance, 'getEntriesByType', { value: undefined, configurable: true });
    try {
      expect(DeskApi.info(new HttpResponse(), 0).bytes).toBeNull();
    } finally {
      Object.defineProperty(performance, 'getEntriesByType', { value: original, configurable: true });
    }
  });
});
