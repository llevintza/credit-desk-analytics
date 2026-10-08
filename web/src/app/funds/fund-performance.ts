import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { AgGridAngular } from 'ag-grid-angular';
import type { GridOptions } from 'ag-grid-community';
import { catchError, map, of, startWith, switchMap } from 'rxjs';
import { ScopeService } from '../core/scope.service';
import { ThemeService } from '../core/theme.service';
import { FundPerformance as Perf, FundRange } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';
import { deskGridTheme, registerGridModules } from '../positions/grid-setup';
import { fundColumns, fundRows } from './fund-columns';

registerGridModules();

export const ranges: FundRange[] = ['QTD', 'YTD', '1Y', 'ITD', 'CUSTOM'];

interface Query { fundId: number; range: FundRange; from: string; to: string; }
type State = { kind: 'loading' } | { kind: 'ready'; perf: Perf } | { kind: 'error' } | { kind: 'incomplete' };

/**
 * P2 Fund Performance (README §6): balance and IRR by month-end for a range. The month columns are built from
 * the response in a `computed()`; a new range or fund supersedes the request in flight (`switchMap`), so a quick
 * flip through ranges can never paint a stale one.
 */
@Component({
  selector: 'app-fund-performance',
  imports: [AgGridAngular],
  templateUrl: './fund-performance.html',
  styleUrl: './fund-performance.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FundPerformancePage {
  private readonly api = inject(DeskApi);
  protected readonly scope = inject(ScopeService);
  private readonly theme = inject(ThemeService);

  protected readonly ranges = ranges;
  protected readonly fundId = signal<number | null>(null);
  protected readonly range = signal<FundRange>('YTD');
  protected readonly from = signal('');
  protected readonly to = signal('');

  /** Null while the query is incomplete (no fund yet, or CUSTOM without both months). */
  private readonly query = computed<Query | null>(() => {
    const fundId = this.fundId();
    if (fundId === null) return null;
    const q = { fundId, range: this.range(), from: this.from(), to: this.to() };
    return q.range === 'CUSTOM' && !(q.from && q.to) ? null : q;
  });

  // An incomplete query (CUSTOM without both months) is a state of its own: the previous response must not stay
  // on screen under the new selection (README §6 P2: never show a stale range).
  protected readonly state = toSignal(
    toObservable(this.query).pipe(
      switchMap((q) => q === null ? of<State>({ kind: this.fundId() === null ? 'loading' : 'incomplete' }) :
        this.api.fundPerformance(q.fundId, q.range, q.from && `${q.from}-01`, q.to && `${q.to}-01`).pipe(
          map((perf): State => ({ kind: 'ready', perf })),
          catchError(() => of<State>({ kind: 'error' })),
          startWith<State>({ kind: 'loading' }),
        )),
    ),
    { initialValue: { kind: 'loading' } as State },
  );

  /** What the grid shows for the current response: the data plus columns and rows derived from its months. */
  protected readonly view = computed(() => {
    const s = this.state();
    // Read the negative style here so a change rebuilds the columns and the grid repaints the numbers.
    const negatives = this.theme.negatives();
    if (s.kind !== 'ready') return null;
    return { perf: s.perf, columns: fundColumns(s.perf.months, () => negatives), rows: fundRows(s.perf) };
  });

  protected readonly gridOptions: GridOptions = {
    theme: deskGridTheme,
    rowModelType: 'clientSide',
    getRowId: (p) => p.data.label,
    domLayout: 'autoHeight',
    suppressMovableColumns: true,
  };

  constructor() {
    this.scope.load();
    // Default to the first fund once the entitled funds are known.
    effect(() => {
      const first = this.scope.funds()[0]?.fundId;
      untracked(() => {
        if (first !== undefined && this.fundId() === null) this.fundId.set(first);
      });
    });
  }

  protected setFund(event: Event): void {
    this.fundId.set(Number((event.target as HTMLSelectElement).value));
  }

  protected setMonth(which: 'from' | 'to', event: Event): void {
    (which === 'from' ? this.from : this.to).set((event.target as HTMLInputElement).value);
  }
}
