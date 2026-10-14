import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { AgGridAngular } from 'ag-grid-angular';
import { ModuleRegistry, QuickFilterModule, type FilterChangedEvent, type GridApi, type GridOptions, type GridReadyEvent, type RowClickedEvent } from 'ag-grid-community';
import { Observable, catchError, debounceTime, distinctUntilChanged, map, of, startWith, switchMap } from 'rxjs';
import { formatCell, fullPrecision } from '../core/format';
import { KeyboardService } from '../core/keyboard.service';
import { ScopeService } from '../core/scope.service';
import { StatusService } from '../core/status.service';
import { ThemeService } from '../core/theme.service';
import { DealDetail, DealList, DealRow } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';
import { monthHeader } from '../funds/fund-columns';
import { deskGridTheme, registerGridModules } from '../positions/grid-setup';
import { capitalStack, dealListColumns, positionColumns, trancheColumns, trendRows } from './deal-columns';

registerGridModules();
ModuleRegistry.registerModules([QuickFilterModule]); // Community

export type Loaded<T> = { kind: 'loading' } | { kind: 'error' } | { kind: 'ready'; value: T };
export type DetailState = Loaded<DealDetail> | { kind: 'none' };

/** A request as a state stream: loading first, then the value or an error state (one failure never blanks the page). */
export function loaded<T>(request: Observable<T>): Observable<Loaded<T>> {
  return request.pipe(
    map((value): Loaded<T> => ({ kind: 'ready', value })),
    catchError(() => of<Loaded<T>>({ kind: 'error' })),
    startWith<Loaded<T>>({ kind: 'loading' }),
  );
}

interface Scope { asOf: string | null; portfolioIds: number[]; }

/**
 * P4 Deal Explorer (README §6): every deal with its most senior bond (zero-bond deals included, blank bond columns),
 * and, for the selected deal, its capital structure (stack + grid), 12-month surveillance trend and the scope's
 * positions. List and detail are separate `switchMap` streams, so a new scope or a new selection supersedes the
 * request in flight and a stale deal can never paint over the current one.
 */
@Component({
  selector: 'app-deal-explorer',
  imports: [AgGridAngular],
  templateUrl: './deal-explorer.html',
  styleUrl: './deal-explorer.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DealExplorer {
  private readonly api = inject(DeskApi);
  protected readonly scope = inject(ScopeService);
  private readonly theme = inject(ThemeService);
  private readonly status = inject(StatusService);
  private readonly keyboard = inject(KeyboardService);
  private readonly quickInput = viewChild<ElementRef<HTMLInputElement>>('quickInput');
  private grid?: GridApi;

  private readonly retry = signal(0);
  protected readonly selectedId = signal<number | null>(null);
  protected readonly quick = signal('');
  /** Typed input settles before the grid refilters (README §9.4). */
  protected readonly quickText = toSignal(toObservable(this.quick).pipe(debounceTime(150), distinctUntilChanged()), { initialValue: '' });

  /** Null until the shell's scope (as-of, portfolios) is known. */
  private readonly base = computed<Scope | null>(() =>
    this.scope.ready() ? { asOf: this.scope.asOf(), portfolioIds: this.scope.selected() } : null);

  protected readonly list = toSignal(
    toObservable(computed(() => ({ base: this.base(), retry: this.retry() }))).pipe(
      switchMap(({ base }) => base === null ? of<Loaded<DealList>>({ kind: 'loading' }) : loaded(this.api.deals(base.asOf, base.portfolioIds))),
    ),
    { initialValue: { kind: 'loading' } as Loaded<DealList> },
  );

  protected readonly detail = toSignal(
    toObservable(computed(() => {
      const base = this.base();
      const id = this.selectedId();
      return base === null || id === null ? null : { ...base, id, retry: this.retry() };
    })).pipe(
      switchMap((q): Observable<DetailState> => q === null ? of({ kind: 'none' }) : loaded(this.api.deal(q.id, q.asOf, q.portfolioIds))),
    ),
    { initialValue: { kind: 'none' } as DetailState },
  );

  protected readonly rows = computed(() => {
    const s = this.list();
    return s.kind === 'ready' ? s.value.deals : [];
  });
  protected readonly summary = computed(() => {
    const deals = this.rows();
    return { deals: deals.length, zeroBond: deals.filter((d) => d.bondCount === 0).length, held: deals.filter((d) => d.positions !== null).length };
  });

  /** Column defs follow the negative-number style, so a settings change repaints every grid. */
  private readonly negatives = this.theme.negatives;
  protected readonly listColumns = computed(() => { const n = this.negatives(); return dealListColumns(() => n); });
  protected readonly trancheCols = computed(() => { const n = this.negatives(); return trancheColumns(() => n); });
  protected readonly positionCols = computed(() => { const n = this.negatives(); return positionColumns(() => n); });

  /** The selected deal, with its stack and trend derived once per response (no work in the template). */
  protected readonly view = computed(() => {
    const s = this.detail();
    if (s.kind !== 'ready') return null;
    const d = s.value;
    const held = d.positions.reduce((sum, p) => sum + (p.marketValue ?? 0), 0);
    return {
      ...d,
      stack: capitalStack(d.tranches),
      trend: trendRows(d.surveillance),
      months: d.surveillance.map((m) => ({ period: m.period, label: monthHeader(m.period) })),
      heldMv: d.positions.length ? held : null,
    };
  });

  protected readonly listOptions: GridOptions<DealRow> = {
    theme: deskGridTheme,
    rowModelType: 'clientSide',
    getRowId: (p) => String(p.data.dealId),
    suppressMovableColumns: true,
    rowClassRules: { 'selected-row': (p) => p.data?.dealId === this.selectedId() },
    tooltipShowDelay: 400,
  };

  protected readonly detailOptions: GridOptions = {
    theme: deskGridTheme,
    rowModelType: 'clientSide',
    domLayout: 'autoHeight',
    suppressMovableColumns: true,
    tooltipShowDelay: 400,
  };
  protected readonly trancheOptions: GridOptions = { ...this.detailOptions, getRowId: (p) => String(p.data.bondId) };
  protected readonly positionOptions: GridOptions = { ...this.detailOptions, getRowId: (p) => String(p.data.positionId) };

  constructor() {
    this.scope.load();
    // The highlighted row follows the selection.
    effect(() => {
      this.selectedId();
      untracked(() => this.grid?.redrawRows());
    });
    effect(() => {
      const s = this.summary();
      untracked(() => {
        this.status.totalRows.set(s.deals);
        this.status.visibleRows.set(this.grid ? this.grid.getDisplayedRowCount() : s.deals);
      });
    });
    this.keyboard.register({ focusQuickFilter: () => this.quickInput()?.nativeElement.focus() });
    inject(DestroyRef).onDestroy(() => {
      this.keyboard.clear();
      this.status.reset();
    });
  }

  protected onListReady(e: GridReadyEvent): void {
    this.grid = e.api;
  }

  protected onFilterChanged(e: FilterChangedEvent): void {
    this.status.visibleRows.set(e.api.getDisplayedRowCount());
  }

  protected select(e: RowClickedEvent<DealRow>): void {
    if (e.data) this.selectedId.set(e.data.dealId);
  }

  protected onQuick(event: Event): void {
    this.quick.set((event.target as HTMLInputElement).value);
  }

  protected reload(): void {
    this.retry.update((n) => n + 1);
  }

  protected money(value: number | null): string {
    return formatCell('Money', value, this.negatives());
  }

  protected pct(value: number | null): string {
    return formatCell('Pct', value, this.negatives());
  }

  protected cell(kind: 'Pct' | 'Count', value: number | null): string {
    return formatCell(kind, value, this.negatives());
  }

  protected exact(kind: 'Pct' | 'Count' | 'Money', value: number | null): string {
    return fullPrecision(kind, value);
  }
}
