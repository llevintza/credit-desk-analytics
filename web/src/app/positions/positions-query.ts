import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { IDatasource, IGetRowsParams } from 'ag-grid-community';
import { HttpErrorResponse } from '@angular/common/http';
import { BehaviorSubject, Subject, catchError, debounceTime, distinctUntilChanged, filter, mergeMap, of, retry, switchMap, tap, throwError, timer } from 'rxjs';
import { GridBlock, GridRequest, RequestInfo, SortSpec } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';
import { toRows } from '../data-access/to-rows';

/** Everything that defines the rows the grid shows, apart from paging (README §6 P1). */
export interface PositionsView {
  asOf: string | null;
  portfolioIds: number[];
  columns: string[];
  quickFilter: string;
  sortModel: SortSpec[];
  filterModel: Record<string, unknown>;
}

interface BlockRequest { view: PositionsView; params: Pick<IGetRowsParams, 'startRow' | 'endRow' | 'successCallback' | 'failCallback'>; }

export const quickFilterDebounceMs = 300;
/** A 429 (README §7.2 limits) is retried after the server's Retry-After, at most this many times. */
export const maxRateLimitRetries = 2;

/** Seconds to wait before retrying a rate-limited request: the server's Retry-After, at least 1 s, at most 30 s. */
export function retryAfterMs(error: unknown): number | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 429) return null;
  const seconds = Number(error.headers.get('Retry-After'));
  return Math.min(30, Math.max(1, Number.isFinite(seconds) ? seconds : 1)) * 1000;
}

/**
 * Feeds AG Grid's Infinite Row Model (ADR-0009). Each view (columns, quick filter, sort, filter, as-of, portfolios)
 * is a new object on `view$`; `switchMap` drops the previous view's pipeline, which unsubscribes — and so aborts —
 * every block request still in flight for it. Blocks of the current view load in parallel (`mergeMap`).
 * Provided per page, so it lives and dies with the grid.
 */
@Injectable()
export class PositionsQuery {
  private readonly api = inject(DeskApi);
  private readonly view$ = new BehaviorSubject<PositionsView>({
    asOf: null, portfolioIds: [], columns: [], quickFilter: '', sortModel: [], filterModel: {},
  });
  private readonly blocks$ = new Subject<BlockRequest>();
  private readonly typed$ = new Subject<string>();

  /** The last block's totals, row count and request details, for the summary row and the status bar. */
  readonly lastBlock = signal<GridBlock | null>(null);
  readonly lastInfo = signal<RequestInfo | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  /** Requests that completed (tests and the e2e "exactly one request" assertion read this). */
  readonly completed = signal(0);

  /** Set by the page: purge the grid's block cache so it asks again for the new view. */
  onViewChanged: () => void = () => undefined;

  constructor() {
    this.view$.pipe(
      switchMap((view) => this.blocks$.pipe(
        filter((r) => r.view === view),
        mergeMap((r) => this.fetch(r)),
      )),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe();

    this.typed$.pipe(
      debounceTime(quickFilterDebounceMs),
      distinctUntilChanged(),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe((quickFilter) => this.update({ quickFilter }));
  }

  get view(): PositionsView {
    return this.view$.value;
  }

  /** Quick filter input (README §6: debounced, only the latest value is requested). */
  type(text: string): void {
    this.typed$.next(text.trim());
  }

  /** Only a change to the *set* of displayed columns needs new data; reordering or resizing doesn't. */
  setColumns(columns: string[]): void {
    const now = [...columns].sort().join(',');
    if (now !== [...this.view.columns].sort().join(',')) this.update({ columns });
  }

  update(change: Partial<PositionsView>): void {
    this.view$.next({ ...this.view, ...change });
    this.onViewChanged();
  }

  readonly datasource: IDatasource = {
    getRows: (params) => {
      // The grid owns sort and filter: when they differ, that's a new view (and older blocks are aborted).
      const sortModel = params.sortModel.map((s) => ({ colId: s.colId, sort: s.sort })) as SortSpec[];
      if (JSON.stringify(sortModel) !== JSON.stringify(this.view.sortModel) || JSON.stringify(params.filterModel) !== JSON.stringify(this.view.filterModel)) {
        this.view$.next({ ...this.view, sortModel, filterModel: params.filterModel as Record<string, unknown> });
      }
      this.blocks$.next({ view: this.view, params });
    },
  };

  request(view: PositionsView, startRow: number, endRow: number): GridRequest {
    return {
      ...(view.asOf ? { asOf: view.asOf } : {}),
      ...(view.portfolioIds.length ? { portfolioIds: view.portfolioIds } : {}),
      startRow, endRow,
      columns: view.columns,
      ...(view.sortModel.length ? { sortModel: view.sortModel } : {}),
      ...(Object.keys(view.filterModel).length ? { filterModel: view.filterModel } : {}),
      ...(view.quickFilter ? { quickFilter: view.quickFilter } : {}),
    };
  }

  /** The current view without paging: what the CSV export streams. */
  exportRequest(): Omit<GridRequest, 'startRow' | 'endRow'> {
    const request: Partial<GridRequest> = this.request(this.view, 0, 0);
    delete request.startRow;
    delete request.endRow;
    return request as Omit<GridRequest, 'startRow' | 'endRow'>;
  }

  private fetch({ view, params }: BlockRequest) {
    this.loading.set(true);
    return this.api.positions(this.request(view, params.startRow, params.endRow)).pipe(
      retry({
        count: maxRateLimitRetries,
        delay: (error: unknown) => {
          const wait = retryAfterMs(error);
          return wait === null ? throwError(() => error) : timer(wait);
        },
      }),
      tap(({ block, info }) => {
        this.loading.set(false);
        this.error.set(null);
        this.lastBlock.set(block);
        this.lastInfo.set(info);
        this.completed.update((n) => n + 1);
        params.successCallback(toRows(block), block.rowCount);
      }),
      catchError(() => {
        // One failed block shows an error tile; the rest of the page keeps working (README §9.4).
        this.loading.set(false);
        this.error.set('Could not load positions. Scroll or change the view to retry.');
        params.failCallback();
        return of(null);
      }),
    );
  }
}
