import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { DeskApi } from '../data-access/desk-api';
import { Mark, MarkTotals } from './marks';
import { MarksOverlay, idleMs, maxRetries, retryMs, totalsMs } from './marks-overlay';

interface OpenStream { portfolioIds: number[]; lastEventId: number | null; text: string; subject: Subject<string>; }

/** DeskApi's two overlay calls, each answered by hand through a Subject. */
class FakeApi {
  readonly streams: OpenStream[] = [];
  readonly totals: { request: unknown; subject: Subject<MarkTotals> }[] = [];

  markStream(portfolioIds: number[], lastEventId: number | null) {
    const stream: OpenStream = { portfolioIds, lastEventId, text: '', subject: new Subject<string>() };
    this.streams.push(stream);
    return stream.subject;
  }

  markTotals(request: unknown) {
    const subject = new Subject<MarkTotals>();
    this.totals.push({ request, subject });
    return subject;
  }

  get last(): OpenStream {
    return this.streams[this.streams.length - 1];
  }

  /** Appends events to the last stream's text and emits the whole text so far, like the fetch progress events. */
  send(...events: [string, unknown][]) {
    const s = this.last;
    s.text += events.map(([event, data]) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`).join('');
    s.subject.next(s.text);
  }
}

const mark = (bondId: number, seq: number, price = 100): Mark => ({ bondId, seq, eventTs: 't', price, spreadBp: 200 });
const hello = (bonds = 2) => ['hello', { asOf: '2026-10-06', bonds, tickMs: 1000, endsAt: 'x', seq: 10 }] as [string, unknown];
const totals = (seq: number, price: number): MarkTotals => ({ asOf: '2026-10-06', seq, eventTs: 't', rowCount: 3, summary: { price } });

describe('MarksOverlay', () => {
  let api: FakeApi;
  let overlay: MarksOverlay;
  let applied: Mark[][];

  beforeEach(() => {
    vi.useFakeTimers();
    api = new FakeApi();
    TestBed.configureTestingModule({ providers: [MarksOverlay, { provide: DeskApi, useValue: api }] });
    overlay = TestBed.inject(MarksOverlay);
    applied = [];
    overlay.onMarks = (marks) => applied.push(marks);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
  });

  it('opens the stream for the portfolios, goes live on hello and hands over each applied chunk', () => {
    expect(overlay.status()).toBe('off');
    overlay.start([3, 7]);
    expect(overlay.status()).toBe('connecting');
    expect(api.last).toMatchObject({ portfolioIds: [3, 7], lastEventId: null });

    api.send(hello(2), ['mark', mark(1, 10)], ['mark', mark(2, 9)]);
    expect(overlay.status()).toBe('live');
    expect(overlay.bonds()).toBe(2);
    expect(applied).toEqual([[mark(1, 10), mark(2, 9)]]);
    expect(overlay.enabled).toBe(true);
  });

  it('never applies a mark at or below the seq already applied for its bond', () => {
    overlay.start([]);
    api.send(hello(), ['mark', mark(1, 10, 101)]);
    api.send(['mark', mark(1, 10, 99)], ['mark', mark(1, 8, 98)]);
    expect(applied).toEqual([[mark(1, 10, 101)]]); // the stale chunk applied nothing, so nothing was handed over
    expect(overlay.book.get(1)?.price).toBe(101);
  });

  it('reconnects at the stream lifetime from the last seq and drops the stale part of the replay', () => {
    overlay.start([]);
    api.send(hello(), ['mark', mark(1, 10)], ['mark', mark(2, 12)]);
    api.send(['bye', { reason: 'lifetime' }]);
    api.last.subject.complete();
    expect(api.streams.length).toBe(2);
    expect(api.last.lastEventId).toBe(12);

    // A server that replays from an older point (another instance, a restart) can't put an older mark back.
    api.send(hello(), ['mark', mark(1, 9)], ['mark', mark(2, 12)], ['mark', mark(1, 13, 105)]);
    expect(applied[1]).toEqual([mark(1, 13, 105)]);
    expect(overlay.status()).toBe('live');
  });

  it('stays away when the scope has nothing to mark', () => {
    overlay.start([]);
    api.send(hello(0), ['bye', { reason: 'empty' }]);
    api.last.subject.complete();
    expect(overlay.status()).toBe('empty');
    vi.advanceTimersByTime(10 * retryMs);
    expect(api.streams.length).toBe(1);
  });

  it('retries a cut-off stream after the retry wait and a refused one after its Retry-After, then gives up', () => {
    overlay.start([]);
    api.send(hello());
    api.last.subject.complete(); // cut off without a bye
    expect(overlay.status()).toBe('connecting');
    vi.advanceTimersByTime(retryMs - 1);
    expect(api.streams.length).toBe(1);
    vi.advanceTimersByTime(1);
    expect(api.streams.length).toBe(2);

    const busy = new HttpErrorResponse({ status: 429, headers: new HttpHeaders({ 'Retry-After': '3' }) });
    api.last.subject.error(busy);
    vi.advanceTimersByTime(3000);
    expect(api.streams.length).toBe(3);

    for (let i = 0; i < maxRetries; i++) {
      api.last.subject.error(new HttpErrorResponse({ status: 503 }));
      vi.advanceTimersByTime(retryMs);
    }
    expect(overlay.status()).toBe('error');
    const opened = api.streams.length;
    vi.advanceTimersByTime(10 * retryMs);
    expect(api.streams.length).toBe(opened);
  });

  it('pauses instead of reconnecting once its user has been idle, and resumes on the next input', () => {
    overlay.start([]);
    api.send(hello(), ['mark', mark(1, 10)]);
    vi.advanceTimersByTime(idleMs);
    api.send(['bye', { reason: 'lifetime' }]);
    api.last.subject.complete();
    expect(overlay.status()).toBe('paused');
    expect(api.streams.length).toBe(1);

    document.dispatchEvent(new KeyboardEvent('keydown'));
    expect(api.streams.length).toBe(2);
    expect(api.last.lastEventId).toBe(10);
    expect(overlay.status()).toBe('connecting');
  });

  it('a dropped stream of an idle user pauses after its wait instead of reconnecting', () => {
    overlay.start([]);
    api.send(hello());
    vi.advanceTimersByTime(idleMs);
    api.last.subject.error(new HttpErrorResponse({ status: 502 }));
    vi.advanceTimersByTime(retryMs);
    expect(overlay.status()).toBe('paused');
    expect(api.streams.length).toBe(1);
  });

  it('closes the stream while the tab is hidden and opens it again when it is shown', () => {
    overlay.start([]);
    api.send(hello());
    const first = api.last.subject;
    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    expect(overlay.status()).toBe('paused');
    expect(first.observed).toBe(false);

    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    expect(api.streams.length).toBe(2);
  });

  it('starts paused in a hidden tab, and a hidden tab does not wake a stopped overlay', () => {
    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    expect(overlay.status()).toBe('off');
    overlay.start([]);
    expect(overlay.status()).toBe('paused');
    expect(api.streams.length).toBe(0);
  });

  it('refreshes the totals from the server periodically, keeping only newer ones, for the current view', () => {
    let request: { columns: string[] } | null = { columns: ['price'] };
    overlay.totalsRequest = () => request;
    overlay.start([]);
    vi.advanceTimersByTime(0);
    expect(api.totals.length).toBe(1); // at once
    api.totals[0].subject.next(totals(30, 99.5));
    expect(overlay.totals()?.summary).toEqual({ price: 99.5 });

    vi.advanceTimersByTime(totalsMs);
    expect(api.totals.length).toBe(2);
    api.totals[1].subject.next(totals(15, 1)); // an older window: ignored
    expect(overlay.totals()?.seq).toBe(30);
    api.totals[1].subject.next(totals(45, 99.7));
    expect(overlay.totals()?.seq).toBe(45);

    // A failed refresh keeps the last totals and the timer going.
    vi.advanceTimersByTime(totalsMs);
    api.totals[2].subject.error(new Error('boom'));
    expect(overlay.totals()?.seq).toBe(45);

    // A new view: its old totals go at once, and its own are asked for right away.
    overlay.viewChanged();
    expect(overlay.totals()).toBeNull();
    vi.advanceTimersByTime(0);
    expect(api.totals.length).toBe(4);
    api.totals[3].subject.next(totals(45, 98));
    expect(overlay.totals()?.summary).toEqual({ price: 98 });

    // No marked column on screen: nothing to ask.
    request = null;
    vi.advanceTimersByTime(totalsMs);
    expect(api.totals.length).toBe(4);
  });

  it('without a page listener marks are still applied, and events it does not know are ignored', () => {
    const bare = TestBed.runInInjectionContext(() => new MarksOverlay());
    bare.start([]);
    api.send(hello(), ['ping', { x: 1 }], ['mark', mark(1, 10)]);
    expect(bare.book.get(1)?.seq).toBe(10);
    expect(bare.status()).toBe('live');
  });

  it('stop during a retry wait cancels the retry', () => {
    overlay.start([]);
    api.last.subject.error(new HttpErrorResponse({ status: 503 }));
    overlay.stop();
    vi.advanceTimersByTime(10 * retryMs);
    expect(api.streams.length).toBe(1);
    expect(overlay.status()).toBe('off');
  });

  it('stop closes everything and forgets the marks and totals', () => {
    overlay.totalsRequest = () => ({ columns: ['price'] });
    overlay.start([]);
    api.send(hello(), ['mark', mark(1, 10)]);
    vi.advanceTimersByTime(0);
    api.totals[0].subject.next(totals(30, 99));
    overlay.stop();
    expect(overlay.status()).toBe('off');
    expect(overlay.book.size).toBe(0);
    expect(overlay.totals()).toBeNull();
    expect(overlay.bonds()).toBe(0);
    expect(api.last.subject.observed).toBe(false);
    overlay.viewChanged(); // nothing to refresh while off
    vi.advanceTimersByTime(totalsMs);
    expect(api.totals.length).toBe(1);
    document.dispatchEvent(new KeyboardEvent('keydown')); // input does not turn it back on
    expect(api.streams.length).toBe(1);
  });
});
