import { ChangeDetectionStrategy, Component, Injector, Signal, afterNextRender, computed, effect, inject, isDevMode, signal, untracked } from '@angular/core';
import { LowerCasePipe } from '@angular/common';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { AuthService } from '../core/auth.service';
import { ScopeService } from '../core/scope.service';
import { ThemeService } from '../core/theme.service';
import { InsightGrid, InsightSource } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';
import { formatInsight, insightTooltip, isNegative, isNumericFormat } from './insight-format';
import { BoardQuery, SourceState, insightSources, sourceStream } from './source-stream';

/** One tile as the template draws it. */
type Tile =
  | { kind: 'loading'; id: string }
  | { kind: 'error'; id: string }
  | { kind: 'ready'; id: string; grid: InsightGrid };

/**
 * P3 Insights Board (README §6): 20 small grids from five data sources, one request per source. Each source is its
 * own stream, so tiles paint progressively: a slow source shows skeletons while the rest are drawn, and a failing
 * one shows error tiles with a retry without touching the others. Tiles are plain tables (ADR-0012).
 */
@Component({
  selector: 'app-insights-board',
  imports: [LowerCasePipe],
  templateUrl: './insights-board.html',
  styleUrl: './insights-board.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InsightsBoard {
  private readonly api = inject(DeskApi);
  private readonly auth = inject(AuthService);
  private readonly injector = inject(Injector);
  protected readonly scope = inject(ScopeService);
  private readonly theme = inject(ThemeService);

  protected readonly sources = insightSources;
  /** Naive mode (dev or admin only): 20 requests, one per grid, for the Performance Lab comparison. */
  protected readonly naive = signal(false);
  protected readonly canNaive = computed(() => this.auth.isAdmin() || isDevMode());
  private readonly retries = Object.fromEntries(insightSources.map((s) => [s.source, signal(0)])) as Record<InsightSource, ReturnType<typeof signal<number>>>;

  /** The shared inputs: as-of and portfolios from the shell scope. Null until the scope is known. */
  private readonly base = computed(() => {
    if (!this.scope.ready()) return null;
    return { asOf: this.scope.asOf(), portfolioIds: this.scope.selected(), naive: this.naive() && this.canNaive() };
  });

  protected readonly states = Object.fromEntries(insightSources.map(({ source, grids }) => {
    const query = computed<BoardQuery | null>(() => {
      const b = this.base();
      return b === null ? null : { ...b, retry: this.retries[source]() };
    });
    return [source, toSignal(sourceStream(this.api, source, grids, toObservable(query)), { initialValue: { kind: 'loading' } as SourceState })];
  })) as Record<InsightSource, Signal<SourceState>>;

  /** Every source's tiles in board order, derived from the five states. */
  protected readonly tiles = computed(() => insightSources.map(({ source, label, grids }) => {
    const state = this.states[source]();
    return {
      source, label,
      tiles: grids.map((id): Tile => {
        if (state.kind === 'error') return { kind: 'error', id };
        // By id, not position, so a change in the API's grid order can't put a grid in the wrong tile.
        const grid = state.kind === 'ready' ? state.grids.find((g) => g?.id === id) : null;
        return grid ? { kind: 'ready', id, grid } : { kind: 'loading', id };
      }),
    };
  }));

  protected readonly negatives = this.theme.negatives;

  constructor() {
    this.scope.load();
    // README §10 P3 budget (first tile / all tiles), read by the e2e: a mark when the inputs change and one per
    // source once its tiles are painted.
    effect(() => {
      const b = this.base();
      untracked(() => {
        if (b === null) return;
        // Only the board's own marks: other pages' marks stay.
        for (const m of performance.getEntriesByType('mark')) if (m.name.startsWith('insights:')) performance.clearMarks(m.name);
        performance.mark('insights:start');
      });
    });
    for (const { source } of insightSources) {
      effect(() => {
        const s = this.states[source]();
        const done = s.kind === 'ready' && s.grids.every((g) => g !== null);
        if (done) afterNextRender(() => performance.mark(`insights:painted:${source}`), { injector: this.injector });
      });
    }
  }

  protected retry(source: InsightSource): void {
    this.retries[source].update((n) => n + 1);
  }

  protected toggleNaive(): void {
    this.naive.update((n) => !n);
  }

  protected text(grid: InsightGrid, col: number, value: string | number | null): string {
    return formatInsight(grid.format[grid.columns[col]], value, this.negatives());
  }

  protected tooltip(grid: InsightGrid, col: number, value: string | number | null): string {
    return insightTooltip(grid.format[grid.columns[col]], value);
  }

  protected numeric(grid: InsightGrid, col: number): boolean {
    return isNumericFormat(grid.format[grid.columns[col]]);
  }

  protected readonly isNegative = isNegative;
}
