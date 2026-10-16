import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { LabClient, ResourceTimings, tagOf, timingWaitMs } from './lab-client';

const entry = (name: string) => ({ name, entryType: 'resource', encodedBodySize: 10 }) as unknown as PerformanceResourceTiming;

describe('Performance Lab client (README §6 P5)', () => {
  afterEach(() => vi.useRealTimers());

  function setup() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), ResourceTimings, LabClient] });
    return { timings: TestBed.inject(ResourceTimings), client: TestBed.inject(LabClient), http: TestBed.inject(HttpTestingController) };
  }

  it('reads the lab tag from a resource URL', () => {
    expect(tagOf('https://api.example.test/api/positions/query?lab=abc-1')).toBe('abc-1');
    expect(tagOf('/api/insights/core?asOf=2026-10-06&lab=x-2')).toBe('x-2');
    expect(tagOf('/api/positions/query')).toBeNull();
    expect(tagOf('/api/insights/core?asOf=2026-10-06')).toBeNull();
  });

  it('hands out an entry already reported, once', async () => {
    const { timings } = setup();
    const e = entry('/api/positions/query?lab=a-1');
    timings.add([e, entry('/api/positions/query'), entry('/other?x=1')]);
    expect(await timings.entry('a-1')).toBe(e);
    vi.useFakeTimers();
    const again = timings.entry('a-1');
    vi.advanceTimersByTime(timingWaitMs);
    expect(await again).toBeNull();
  });

  it('listens to the browser through a PerformanceObserver and stops when destroyed', async () => {
    let callback: ((list: { getEntries: () => PerformanceEntryList }) => void) | undefined;
    const observe = vi.fn();
    const disconnect = vi.fn();
    vi.stubGlobal('PerformanceObserver', class {
      constructor(cb: typeof callback) { callback = cb; }
      observe = observe;
      disconnect = disconnect;
      takeRecords = () => [];
    });
    try {
      const { timings } = setup();
      expect(observe).toHaveBeenCalledWith({ type: 'resource' });
      const pending = timings.entry('c-3');
      const e = entry('/api/positions/query?lab=c-3');
      callback!({ getEntries: () => [e] });
      expect(await pending).toBe(e);
      TestBed.resetTestingModule();
      expect(disconnect).toHaveBeenCalled();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('waits for an entry the browser reports later', async () => {
    const { timings } = setup();
    const pending = timings.entry('b-2');
    const e = entry('/api/insights/core?lab=b-2');
    timings.add([e]);
    expect(await pending).toBe(e);
  });

  it('sends through HttpClient with the tag, the headers and no browser cache, and returns the bytes and the entry', async () => {
    const { client, http, timings } = setup();
    const sent = client.send('POST', '/api/positions/query', { Accept: 'application/x-msgpack', 'X-Lab-Cache': 'off' }, { endRow: 100 });
    const req = http.expectOne((r) => r.url.startsWith('/api/positions/query?lab='));
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ endRow: 100 });
    expect(req.request.headers.get('Accept')).toBe('application/x-msgpack');
    expect(req.request.headers.get('X-Lab-Cache')).toBe('off');
    expect(req.request.responseType).toBe('arraybuffer');
    expect(req.request.cache).toBe('no-store');
    const tag = tagOf(req.request.url)!;
    const e = entry(`https://api.example.test${req.request.url}`);
    req.flush(new ArrayBuffer(4));
    timings.add([e]);
    const x = await sent;
    expect(x.res.body?.byteLength).toBe(4);
    expect(x.entry).toBe(e);
    expect(tag).toMatch(/^[a-z0-9]+-1$/);

    // A URL with a query string gets the tag appended, and every request has its own.
    const next = client.send('GET', '/api/insights/core?grid=curve_moves', {});
    const second = http.expectOne((r) => r.url.startsWith('/api/insights/core?grid=curve_moves&lab='));
    expect(tagOf(second.request.url)).toMatch(/-2$/);
    second.flush(new ArrayBuffer(0));
    timings.add([entry(second.request.url)]);
    expect((await next).entry).not.toBeNull();
  });

  it('cancels an in-flight request when the page is destroyed', async () => {
    const { client, http } = setup();
    const sent = client.send('POST', '/api/positions/query', {}, { endRow: 100 });
    const req = http.expectOne((r) => r.url.startsWith('/api/positions/query?lab='));
    const settled = expect(sent).rejects.toThrow();
    TestBed.resetTestingModule();
    expect(req.cancelled).toBe(true);
    await settled;
  });
});
