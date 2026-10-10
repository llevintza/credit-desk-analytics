import { Observable, catchError, debounceTime, distinctUntilChanged, map, merge, of, scan, startWith, switchMap } from 'rxjs';
import { InsightGrid, InsightSource } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';

/** The board's grids per source, in the API's order (README §6 P3): skeleton tiles and naive mode need them up front. */
export const insightSources: readonly { source: InsightSource; label: string; grids: readonly string[] }[] = [
  { source: 'core', label: 'Core', grids: ['mv_sector_rating', 'dv01_sector_duration', 'pnl_attribution_portfolio', 'top_issuers_mv', 'vintage_concentration', 'watchlist_sector'] },
  { source: 'market', label: 'Market', grids: ['spread_change_1m', 'curve_moves', 'spread_percentile_2y'] },
  { source: 'surveillance', label: 'Surveillance', grids: ['dq60_sector_vintage', 'cpr_cdr_servicer', 'oc_cushion_buckets', 'warf_clo_vintage', 'ltv_fico_bands'] },
  { source: 'pricing', label: 'Pricing', grids: ['vendor_dispersion', 'internal_vs_vendor', 'challenged_marks'] },
  { source: 'reference', label: 'Reference', grids: ['servicer_exposure', 'trustee_exposure', 'rating_agency_split'] },
];

/** What a source's tiles show. In naive mode grids arrive one by one: a null grid is still loading. */
export type SourceState =
  | { kind: 'loading' }
  | { kind: 'ready'; grids: (InsightGrid | null)[] }
  | { kind: 'error' };

/** The inputs a source reads: null until the scope is known. `retry` bumps re-run the same query. */
export interface BoardQuery { asOf: string | null; portfolioIds: number[]; naive: boolean; retry: number; }

export const debounceMs = 50;

const same = (a: BoardQuery | null, b: BoardQuery | null): boolean => JSON.stringify(a) === JSON.stringify(b);

/**
 * One source's independent stream (README §6 P3): inputs → debounce → switchMap(http) → startWith(loading) →
 * catchError(error tile). A new as-of or scope cancels the request in flight; one source failing or lagging never
 * holds the others back, because each source is its own stream.
 */
export function sourceStream(api: DeskApi, source: InsightSource, grids: readonly string[], query$: Observable<BoardQuery | null>): Observable<SourceState> {
  return query$.pipe(
    debounceTime(debounceMs),
    distinctUntilChanged(same),
    switchMap((q) => q === null ? of<SourceState>({ kind: 'loading' }) : load(api, source, grids, q).pipe(
      startWith<SourceState>({ kind: 'loading' }),
      catchError(() => of<SourceState>({ kind: 'error' })),
    )),
  );
}

function load(api: DeskApi, source: InsightSource, grids: readonly string[], q: BoardQuery): Observable<SourceState> {
  if (!q.naive)
    return api.insights(source, q.asOf, q.portfolioIds).pipe(map((r): SourceState => ({ kind: 'ready', grids: r.grids })));
  // Naive mode (the Performance Lab comparison): one request per grid, each tile painted as its own response lands.
  return merge(...grids.map((id, i) => api.insights(source, q.asOf, q.portfolioIds, id).pipe(map((r) => ({ i, grid: r.grids[0] ?? null }))))).pipe(
    scan((acc, { i, grid }) => acc.map((g, j) => (j === i ? grid : g)), grids.map((): InsightGrid | null => null)),
    map((all): SourceState => ({ kind: 'ready', grids: all })),
  );
}
