import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AgGridAngular } from 'ag-grid-angular';
import type { ColGroupDef, GridApi, GridOptions, GridReadyEvent } from 'ag-grid-community';
import { forkJoin } from 'rxjs';
import { KeyboardService } from '../core/keyboard.service';
import { ScopeService } from '../core/scope.service';
import { StatusService } from '../core/status.service';
import { ThemeService } from '../core/theme.service';
import { Preset } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';
import { columnDefs } from './column-defs';
import { deskGridTheme, registerGridModules } from './grid-setup';
import { PositionsQuery } from './positions-query';
import { initialPreset, nextPreset, page, presetColumnState } from './presets';

registerGridModules();

const rememberKey = 'desk.positions.preset';
export const firstRowsMark = 'positions:first-rows';

/**
 * P1 Start-of-day Positions (README §6): AG Grid Community, Infinite Row Model, only the displayed columns are
 * requested, a pinned summary row over every filtered row, presets, quick filter and CSV export.
 */
@Component({
  selector: 'app-positions',
  imports: [AgGridAngular],
  templateUrl: './positions.html',
  styleUrl: './positions.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [PositionsQuery],
})
export class Positions {
  private readonly api = inject(DeskApi);
  private readonly status = inject(StatusService);
  private readonly scope = inject(ScopeService);
  private readonly theme = inject(ThemeService);
  private readonly keyboard = inject(KeyboardService);
  protected readonly query = inject(PositionsQuery);
  private readonly quick = viewChild<ElementRef<HTMLInputElement>>('quick');

  protected readonly presets = signal<Preset[]>([]);
  protected readonly preset = signal<string>('');
  protected readonly ownPreset = computed(() => this.presets().find((p) => p.name === this.preset())?.builtIn === false);
  protected readonly columns = signal<ColGroupDef[] | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly exporting = signal(false);
  protected readonly saving = signal(false);
  private readonly destroyRef = inject(DestroyRef);
  private grid: GridApi | null = null;
  private readonly gridReady = signal(false);
  private attached = false;
  private painted = false;

  protected readonly gridOptions: GridOptions = {
    theme: deskGridTheme,
    rowModelType: 'infinite',
    cacheBlockSize: 200,
    maxBlocksInCache: 50,
    blockLoadDebounceMillis: 100,
    getRowId: (p) => String(p.data.position_id),
    defaultColDef: { resizable: true, suppressHeaderMenuButton: false },
    tooltipShowDelay: 400,
    suppressColumnVirtualisation: false, // README §6: never disable column virtualization
    animateRows: false,
    rowClassRules: { 'summary-row': (p) => p.node.rowPinned === 'bottom' },
  };

  constructor() {
    this.query.onViewChanged = () => this.grid?.purgeInfiniteCache();
    this.scope.load(); // no-op when the shell already loaded it
    forkJoin({ catalog: this.api.columns(), presets: this.api.presets(page) }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: ({ catalog, presets }) => {
        this.columns.set(columnDefs(catalog, this.theme.negatives));
        this.presets.set(presets);
        this.preset.set(initialPreset(presets, Positions.remembered()));
      },
      error: () => this.loadError.set('Could not load the column catalog or presets. Reload to try again.'),
    });

    // Scope changes (as-of, portfolios) from the top bar are a new view. The grid gets its datasource only once
    // the as-of date is known, so the first view is requested once, not once without it and again with it.
    effect(() => {
      const asOf = this.scope.asOf();
      const portfolioIds = this.scope.selected();
      const ready = this.gridReady() && this.scope.ready();
      untracked(() => {
        if (asOf !== this.query.view.asOf || portfolioIds.join() !== this.query.view.portfolioIds.join())
          this.query.update({ asOf, portfolioIds });
        if (ready && !this.attached) {
          this.attached = true;
          this.query.setColumns(this.displayed());
          this.grid!.setGridOption('datasource', this.query.datasource);
        }
      });
    });

    // The pinned summary row and the status bar follow the last block.
    effect(() => {
      const block = this.query.lastBlock();
      if (!block || !this.grid) return;
      this.grid.setGridOption('pinnedBottomRowData', [{ position_id: -1, deal_name: 'Total', ...block.summary }]);
      this.status.totalRows.set(block.rowCount);
    });
    effect(() => this.status.lastRequest.set(this.query.lastInfo()));
    // Repaint numbers when the negative style changes.
    effect(() => {
      this.theme.negatives();
      untracked(() => this.grid?.refreshCells({ force: true }));
    });

    this.keyboard.register({
      focusQuickFilter: () => this.quick()?.nativeElement.focus(),
      exportCsv: () => this.exportCsv(),
      nextPreset: () => this.applyPreset(nextPreset(this.presets(), this.preset())),
    });
    inject(DestroyRef).onDestroy(() => {
      this.keyboard.clear();
      this.status.reset();
    });
  }

  protected onGridReady(e: GridReadyEvent): void {
    this.grid = e.api;
    this.applyPreset(this.preset());
    this.gridReady.set(true);
  }

  /** Only a change to the displayed *set* reaches the API (PositionsQuery.setColumns); moves and resizes don't. */
  protected onDisplayedColumnsChanged(): void {
    if (this.grid) this.query.setColumns(this.displayed());
  }

  protected onFirstDataRendered(): void {
    if (this.painted) return;
    this.painted = true;
    performance.mark(firstRowsMark); // read by the e2e first-paint measurement (README §10)
  }

  protected onViewportChanged(): void {
    if (!this.grid) return;
    const first = this.grid.getFirstDisplayedRowIndex();
    const last = this.grid.getLastDisplayedRowIndex();
    this.status.visibleRows.set(last >= first ? last - first + 1 : 0);
  }

  protected onQuickInput(event: Event): void {
    this.query.type((event.target as HTMLInputElement).value);
  }

  protected applyPreset(name: string): void {
    const preset = this.presets().find((p) => p.name === name);
    if (!preset || !this.grid) return;
    this.preset.set(name);
    Positions.remember(name);
    this.grid.applyColumnState(presetColumnState(preset.state));
    this.grid.setFilterModel(preset.state.filterModel ?? null);
  }

  protected onPresetChange(event: Event): void {
    this.applyPreset((event.target as HTMLSelectElement).value);
  }

  protected savePresetAs(): void {
    if (!this.grid) return;
    const name = window.prompt('Save the current columns, sort and filters as:', this.ownPreset() ? this.preset() : '')?.trim();
    if (!name) return;
    const state = { columnState: this.grid.getColumnState(), filterModel: this.grid.getFilterModel() };
    this.saving.set(true);
    this.api.savePreset(page, name, state).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.saving.set(false);
        this.loadError.set(null);
        const own: Preset = { name, builtIn: false, state, updatedAt: new Date().toISOString() };
        this.presets.update((list) => [...list.filter((p) => p.name !== name), own]);
        this.preset.set(name);
        Positions.remember(name);
      },
      error: (e: unknown) => {
        this.saving.set(false);
        // The server's problem detail says why (bad name, preset limit, conflict, rate limit); else a generic line.
        const detail = Positions.problemDetail(e);
        this.loadError.set(detail ? `Could not save "${name}": ${detail}` : `Could not save "${name}".`);
      },
    });
  }

  protected deletePreset(): void {
    // Only offered for the user's own presets (the template shows the button for those).
    const name = this.preset();
    this.api.deletePreset(page, name).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.loadError.set(null); // a success clears an earlier failure's tile
        this.presets.update((list) => list.filter((p) => p.name !== name));
        this.applyPreset(initialPreset(this.presets(), null));
      },
      error: () => this.loadError.set(`Could not delete "${name}". Reload and try again.`),
    });
  }

  /** CSV of the current view, streamed by the API (README §6: not the browser cache). */
  protected exportCsv(): void {
    if (this.exporting()) return;
    this.exporting.set(true);
    // The on-screen column order, not the preset's (columns may have been dragged since).
    const columns = this.grid ? this.displayed() : this.query.view.columns;
    this.api.exportPositions(this.query.exportRequest(columns)).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (blob) => {
        this.exporting.set(false);
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `positions-${this.query.view.asOf ?? 'latest'}.csv`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: () => {
        this.exporting.set(false);
        this.loadError.set('Export failed. Only one export runs at a time; try again in a moment.');
      },
    });
  }

  /** Ctrl+C copies the focused cell (Community has no range selection: README §9.3 documents the limit). */
  protected onKeyDown(event: KeyboardEvent): void {
    if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'c' || !this.grid) return;
    const cell = this.grid.getFocusedCell();
    if (!cell) return;
    // The pinned Total row has its own index space: rowIndex 0 there is not body row 0.
    const node = cell.rowPinned === 'bottom' ? this.grid.getPinnedBottomRow(cell.rowIndex) : this.grid.getDisplayedRowAtIndex(cell.rowIndex);
    const value = node?.data?.[cell.column.getColId()];
    void navigator.clipboard?.writeText(value === null || value === undefined ? '' : String(value));
  }

  private displayed(): string[] {
    return this.grid!.getAllDisplayedColumns().map((c) => c.getColId());
  }

  /**
   * The `detail` (else `title`) of an RFC 9457 problem response, or null for a network error or a plain body.
   * A 409's detail alone ("Try again.") doesn't say what clashed, so it keeps the title in front: `title: detail`.
   */
  private static problemDetail(e: unknown): string | null {
    const body: unknown = e instanceof HttpErrorResponse ? e.error : null;
    if (typeof body !== 'object' || body === null) return null;
    const { detail, title } = body as { detail?: unknown; title?: unknown };
    const text = (v: unknown) => (typeof v === 'string' && v ? v : null);
    const [d, t] = [text(detail), text(title)];
    if (d && t && (e as HttpErrorResponse).status === 409) return `${t}: ${d}`;
    return d ?? t;
  }

  private static remembered(): string | null {
    try {
      return localStorage.getItem(rememberKey);
    } catch {
      return null;
    }
  }

  private static remember(name: string): void {
    try {
      localStorage.setItem(rememberKey, name);
    } catch {
      /* not persisted */
    }
  }
}
