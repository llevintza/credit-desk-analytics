import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY, Subscription, catchError, fromEvent, merge, switchMap, timer } from 'rxjs';
import { GridRequest } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';
import { Mark, MarkBook, MarkBye, MarkHello, MarkTotals, SseEvent, SseReader } from './marks';
import { retryAfterMs } from './positions-query';

/**
 * off: not asked for. connecting: opening a stream. live: marks arriving. paused: the tab is hidden or nobody has
 * touched it for {@link idleMs}, so no stream is open. empty: the scope has no bonds to mark. error: gave up.
 */
export type OverlayStatus = 'off' | 'connecting' | 'live' | 'paused' | 'empty' | 'error';

/** No input for this long and the overlay stops reconnecting (ADR-0015: the stream must not keep the free instance awake). */
export const idleMs = 5 * 60_000;
/** How often the totals are refreshed from the server while the overlay is live (README §6 P6). */
export const totalsMs = 30_000;
/** The wait before reconnecting after a dropped stream (the server's SSE `retry`). */
export const retryMs = 5_000;
/** Drops in a row before the overlay gives up and shows an error; a stream that says hello resets the count. */
export const maxRetries = 5;

type TotalsRequest = Omit<GridRequest, 'startRow' | 'endRow'>;

/**
 * The P6 intraday overlay's connection (README §6 P6, ADR-0015). Opens the mark stream for the page's portfolios,
 * applies each mark once (stale ones are dropped by {@link MarkBook}), hands the applied marks to the page, and
 * refreshes the overlay totals every {@link totalsMs}. It keeps the free tier in mind: the server ends every stream
 * after at most five minutes, and the overlay reconnects only while the tab is visible and its user has been
 * active within {@link idleMs}; otherwise it pauses until the next key, click or scroll. Provided per page.
 */
@Injectable()
export class MarksOverlay {
  private readonly api = inject(DeskApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);

  readonly book = new MarkBook();
  readonly status = signal<OverlayStatus>('off');
  readonly bonds = signal(0);
  /** The latest overlay totals for the current view; null until the first arrives or after the view changes. */
  readonly totals = signal<MarkTotals | null>(null);

  /** Set by the page: the marks one chunk of the stream applied, oldest first. */
  onMarks: (marks: Mark[]) => void = () => undefined;
  /** Set by the page: the current view without paging, or null when it shows no marked column. */
  totalsRequest: () => TotalsRequest | null = () => null;

  private readonly reader = new SseReader();
  private portfolioIds: number[] = [];
  private stream: Subscription | null = null;
  private refresh: Subscription | null = null;
  private retry: ReturnType<typeof setTimeout> | null = null;
  private failures = 0;
  private bye: string | null = null;
  private lastActivity = Date.now();

  constructor() {
    merge(...['pointerdown', 'keydown', 'wheel'].map((type) => fromEvent(this.document, type, { passive: true })))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.activity());
    fromEvent(this.document, 'visibilitychange')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => (this.visible() ? this.activity() : this.pause()));
    this.destroyRef.onDestroy(() => this.close());
  }

  get enabled(): boolean {
    return this.status() !== 'off';
  }

  /** Turns the overlay on for these portfolios (empty = every entitled one), from a clean slate. */
  start(portfolioIds: number[]): void {
    this.close();
    this.portfolioIds = portfolioIds;
    this.book.clear();
    this.totals.set(null);
    this.failures = 0;
    this.lastActivity = Date.now();
    this.status.set('connecting');
    if (this.active()) this.resume();
    else this.status.set('paused');
  }

  stop(): void {
    this.close();
    this.book.clear();
    this.totals.set(null);
    this.bonds.set(0);
    this.status.set('off');
  }

  /** The view changed (filter, columns, scope): its old totals no longer apply, so fetch the new view's now. */
  viewChanged(): void {
    this.totals.set(null);
    if (this.refresh) this.refreshTotals();
  }

  /** A key, click or scroll: the user is here. A paused overlay picks up where it stopped. */
  activity(): void {
    this.lastActivity = Date.now();
    if (this.status() === 'paused' && this.visible()) this.resume();
  }

  private resume(): void {
    this.connect();
    this.refreshTotals();
  }

  private pause(): void {
    if (!this.enabled || this.status() === 'empty' || this.status() === 'error') return;
    this.close();
    this.status.set('paused');
  }

  private connect(): void {
    this.closeStream();
    this.reader.reset();
    this.bye = null;
    if (this.status() !== 'live') this.status.set('connecting');
    this.stream = this.api.markStream(this.portfolioIds, this.book.lastSeq)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (text) => this.handle(this.reader.read(text)),
        error: (e: unknown) => this.dropped(e),
        complete: () => this.ended(),
      });
  }

  private handle(events: SseEvent[]): void {
    const applied: Mark[] = [];
    for (const e of events) {
      if (e.event === 'mark') {
        const mark = JSON.parse(e.data) as Mark;
        if (this.book.apply(mark)) applied.push(mark);
      } else if (e.event === 'hello') {
        this.failures = 0;
        this.bonds.set((JSON.parse(e.data) as MarkHello).bonds);
        this.status.set('live');
      } else if (e.event === 'bye') {
        this.bye = (JSON.parse(e.data) as MarkBye).reason;
      }
    }
    if (applied.length) this.onMarks(applied);
  }

  /**
   * The server ended the stream. At its lifetime: come back at once if the user is still here (the marks missed in
   * between are replayed from the last seq). With nothing to mark: stay away. Cut off without a bye (a proxy, a
   * restart): like a dropped stream, after a wait.
   */
  private ended(): void {
    this.stream = null;
    if (this.bye === 'empty') {
      this.close();
      this.status.set('empty');
    } else if (this.bye === null) {
      this.dropped(null);
    } else if (this.active()) {
      this.connect();
    } else {
      this.pause();
    }
  }

  /** A dropped or refused stream: wait (the server's Retry-After on a 429) and try again, a few times. */
  private dropped(error: unknown): void {
    this.stream = null;
    if (++this.failures > maxRetries) {
      this.close();
      this.status.set('error');
      return;
    }
    this.status.set('connecting');
    this.retry = setTimeout(() => {
      this.retry = null;
      if (this.active()) this.connect();
      else this.pause();
    }, retryAfterMs(error) ?? retryMs);
  }

  private refreshTotals(): void {
    this.refresh?.unsubscribe();
    this.refresh = timer(0, totalsMs).pipe(
      switchMap(() => {
        const request = this.totalsRequest();
        return request ? this.api.markTotals(request).pipe(catchError(() => EMPTY)) : EMPTY;
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe((totals) => {
      const shown = this.totals();
      if (!shown || totals.seq > shown.seq) this.totals.set(totals);
    });
  }

  private active(): boolean {
    return this.visible() && Date.now() - this.lastActivity < idleMs;
  }

  private visible(): boolean {
    return this.document.visibilityState !== 'hidden';
  }

  private closeStream(): void {
    this.stream?.unsubscribe();
    this.stream = null;
  }

  private close(): void {
    this.closeStream();
    this.refresh?.unsubscribe();
    this.refresh = null;
    if (this.retry !== null) clearTimeout(this.retry);
    this.retry = null;
  }
}
