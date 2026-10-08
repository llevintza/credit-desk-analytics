import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { AgGridAngular } from 'ag-grid-angular';
import type { ColGroupDef, GridApi, GridOptions, GridReadyEvent } from 'ag-grid-community';
import { forkJoin } from 'rxjs';
import { KeyboardService } from '../core/keyboard.service';
import { ScopeService } from '../core/scope.service';
import { StatusService } from '../core/status.service';
import { ThemeService } from '../core/theme.service';
import { Preset } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';
import { columnDefs, pinnedLeft } from './column-defs';
import { deskGridTheme, registerGridModules } from './grid-setup';
import { PositionsQuery } from './positions-query';
import { columnStateOf, initialPreset, nextPreset, page } from './presets';

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
  private grid: GridApi | null = null;
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
    forkJoin({ catalog: this.api.columns(), presets: this.api.presets(page) }).subscribe({
      next: ({ catalog, presets }) => {
        this.columns.set(columnDefs(catalog, this.theme.negatives));
        this.presets.set(presets);
        this.preset.set(initialPreset(presets, Positions.remembered()));
      },
      error: () => this.loadError.set('Could not load the column catalog or presets. Reload to try again.'),
    });

    // Scope changes (as-of, portfolios) from the top bar are a new view.
    effect(() => {
      const asOf = this.scope.asOf();
      const portfolioIds = this.scope.selected();
      untracked(() => {
        if (asOf !== this.query.view.asOf || portfolioIds.join() !== this.query.view.portfolioIds.join())
          this.query.update({ asOf, portfolioIds });
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
    this.query.setColumns(this.displayed());
    e.api.setGridOption('datasource', this.query.datasource);
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
    const state = columnStateOf(preset.state);
    // Identity columns stay visible and pinned whatever the preset says.
    const pinned = pinnedLeft.map((colId) => ({ colId, hide: false, pinned: 'left' as const }));
    this.grid.applyColumnState({ state: [...pinned, ...state.filter((s) => !pinnedLeft.includes(s.colId))], applyOrder: true, defaultState: { hide: true } });
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
    this.api.savePreset(page, name, state).subscribe({
      next: () => {
        this.saving.set(false);
        const own: Preset = { name, builtIn: false, state, updatedAt: new Date().toISOString() };
        this.presets.update((list) => [...list.filter((p) => p.name !== name), own]);
        this.preset.set(name);
        Positions.remember(name);
      },
      error: () => {
        this.saving.set(false);
        this.loadError.set(`Could not save "${name}". Names must be 1–64 characters and not a built-in name.`);
      },
    });
  }

  protected deletePreset(): void {
    // Only offered for the user's own presets (the template shows the button for those).
    const name = this.preset();
    this.api.deletePreset(page, name).subscribe(() => {
      this.presets.update((list) => list.filter((p) => p.name !== name));
      this.applyPreset(initialPreset(this.presets(), null));
    });
  }

  /** CSV of the current view, streamed by the API (README §6: not the browser cache). */
  protected exportCsv(): void {
    if (this.exporting()) return;
    this.exporting.set(true);
    this.api.exportPositions(this.query.exportRequest()).subscribe({
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
    const value = this.grid.getDisplayedRowAtIndex(cell.rowIndex)?.data?.[cell.column.getColId()];
    void navigator.clipboard?.writeText(value === null || value === undefined ? '' : String(value));
  }

  private displayed(): string[] {
    return this.grid!.getAllDisplayedColumns().map((c) => c.getColId());
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
