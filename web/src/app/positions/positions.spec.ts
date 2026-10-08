import { Component, input, output } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AgGridAngular } from 'ag-grid-angular';
import { KeyboardService } from '../core/keyboard.service';
import { ScopeService } from '../core/scope.service';
import { StatusService } from '../core/status.service';
import { ThemeService } from '../core/theme.service';
import { CatalogColumn, Preset } from '../data-access/api.types';
import { PositionsQuery } from './positions-query';
import { Positions, firstRowsMark } from './positions';

/** Stands in for <ag-grid-angular> in jsdom: same inputs and outputs, no layout engine. */
// eslint-disable-next-line @angular-eslint/component-selector -- must match the real grid's selector to stand in for it
@Component({ selector: 'ag-grid-angular', template: '' })
class StubGrid {
  readonly gridOptions = input<unknown>();
  readonly columnDefs = input<unknown>();
  readonly gridReady = output<unknown>();
  readonly displayedColumnsChanged = output<unknown>();
  readonly firstDataRendered = output<unknown>();
  readonly viewportChanged = output<unknown>();
  readonly bodyScrollEnd = output<unknown>();
}

const catalog: CatalogColumn[] = [
  { name: 'position_id', group: 'Keys', kind: 'Key', aggregation: 'None', header: 'Position' },
  { name: 'deal_name', group: 'Keys', kind: 'Text', aggregation: 'None', header: 'Deal' },
  { name: 'dv01', group: 'Rates', kind: 'Money', aggregation: 'Sum', header: 'DV01' },
];
const presets: Preset[] = [
  { name: 'Risk', builtIn: true, state: { columns: ['deal_name', 'dv01'] }, updatedAt: null },
  { name: 'Mine', builtIn: false, state: { columnState: [{ colId: 'dv01', hide: false }], filterModel: { dv01: { filterType: 'number' } } }, updatedAt: 'x' },
];

function fakeGrid() {
  return {
    purgeInfiniteCache: vi.fn(),
    setGridOption: vi.fn(),
    refreshCells: vi.fn(),
    applyColumnState: vi.fn(),
    setFilterModel: vi.fn(),
    getAllDisplayedColumns: vi.fn(() => ['deal_name', 'dv01'].map((id) => ({ getColId: () => id }))),
    getFirstDisplayedRowIndex: vi.fn(() => 0),
    getLastDisplayedRowIndex: vi.fn(() => 39),
    getFocusedCell: vi.fn((): unknown => ({ rowIndex: 3, column: { getColId: () => 'dv01' } })),
    getDisplayedRowAtIndex: vi.fn((): unknown => ({ data: { dv01: 512.3 } })),
    getColumnState: vi.fn(() => [{ colId: 'dv01', hide: false }]),
    getFilterModel: vi.fn(() => ({})),
  };
}

async function render(opts: { remembered?: string; failLoad?: boolean; scope?: 'loaded' | 'pending' | 'failed' } = {}) {
  localStorage.clear();
  if (opts.remembered) localStorage.setItem('desk.positions.preset', opts.remembered);
  TestBed.configureTestingModule({ imports: [Positions], providers: [provideHttpClient(), provideHttpClientTesting()] });
  TestBed.overrideComponent(Positions, { remove: { imports: [AgGridAngular] }, add: { imports: [StubGrid] } });
  const fixture = TestBed.createComponent(Positions);
  await fixture.whenStable();
  const http = TestBed.inject(HttpTestingController);
  const el = fixture.nativeElement as HTMLElement;
  expect(el.querySelector('.skeleton')).not.toBeNull(); // skeleton while the catalog loads
  const scope = opts.scope ?? 'loaded';
  if (scope === 'loaded') {
    http.expectOne('/api/meta/as-of').flush({ latest: '2026-10-06', dates: ['2026-10-06', '2026-10-05'] });
    http.expectOne('/api/meta/portfolios').flush([]);
  } else if (scope === 'failed') {
    http.expectOne('/api/meta/as-of').flush('boom', { status: 500, statusText: 'x' });
    http.match('/api/meta/portfolios');
  }
  if (opts.failLoad) {
    http.expectOne('/api/meta/columns').flush('boom', { status: 500, statusText: 'x' });
    http.match('/api/presets/positions');
  } else {
    http.expectOne('/api/meta/columns').flush(catalog);
    http.expectOne('/api/presets/positions').flush(presets);
  }
  await fixture.whenStable();
  const grid = fakeGrid();
  const stub = fixture.debugElement.children.find((d) => d.componentInstance instanceof StubGrid)?.componentInstance as StubGrid | undefined;
  const query = fixture.debugElement.injector.get(PositionsQuery);
  const ready = async () => {
    stub!.gridReady.emit({ api: grid });
    await fixture.whenStable();
  };
  return { fixture, el, http, grid, stub, query, ready };
}

/**
 * Must stay the first test in this file: it renders the real component with its compiled (AOT) template and the
 * real AG Grid. The tests below use TestBed.overrideComponent, which recompiles the component in JIT and replaces
 * its compiled definition for the rest of the module, so the AOT template would never run (and its branches
 * would read as uncovered) if this ran after them.
 */
describe('Positions page (real grid, compiled template)', () => {
  beforeAll(() => {
    globalThis.ResizeObserver ??= class {
      observe(): void { /* jsdom: no layout */ }
      unobserve(): void { /* jsdom: no layout */ }
      disconnect(): void { /* jsdom: no layout */ }
    } as unknown as typeof ResizeObserver;
  });

  it('renders the toolbar, then the grid once the catalog is loaded', async () => {
    TestBed.configureTestingModule({ imports: [Positions], providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(Positions);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const http = TestBed.inject(HttpTestingController);
    expect(el.querySelector('.skeleton')).not.toBeNull();
    http.expectOne('/api/meta/as-of').flush({ latest: '2026-10-06', dates: ['2026-10-06'] });
    http.expectOne('/api/meta/portfolios').flush([]);

    http.expectOne('/api/meta/columns').flush([
      { name: 'position_id', group: 'Keys', kind: 'Key', aggregation: 'None', header: 'Position' },
      { name: 'deal_name', group: 'Keys', kind: 'Text', aggregation: 'None', header: 'Deal' },
      { name: 'dv01', group: 'Rates', kind: 'Money', aggregation: 'Sum', header: 'DV01' },
    ]);
    http.expectOne('/api/presets/positions').flush([
      { name: 'Risk', builtIn: true, state: { columns: ['deal_name', 'dv01'] }, updatedAt: null },
      { name: 'Mine', builtIn: false, state: { columns: ['dv01'] }, updatedAt: 'x' },
    ]);
    await fixture.whenStable();
    expect(el.querySelector('[data-testid=positions-grid]')).not.toBeNull();
    expect(el.querySelector('.skeleton')).toBeNull();

    // Drive every conditional block of the compiled template explicitly (not by request timing).
    const component = fixture.componentInstance as unknown as {
      preset: { set(v: string): void }; exporting: { set(v: boolean): void }; loadError: { set(v: string | null): void };
    };
    const query = fixture.debugElement.injector.get(PositionsQuery);
    component.preset.set('Mine');           // own preset: Delete button
    component.exporting.set(true);          // "Exporting…"
    query.loading.set(true);                // "Loading…"
    query.error.set('Could not load positions.');
    await fixture.whenStable();
    expect(el.textContent).toContain('Delete');
    expect(el.textContent).toContain('Exporting');
    expect(el.textContent).toContain('Loading');
    expect(el.querySelector('[role=alert]')?.textContent).toContain('Could not load positions');

    component.loadError.set('Catalog failed.'); // loadError wins over the query error
    await fixture.whenStable();
    expect(el.querySelector('[role=alert]')?.textContent).toContain('Catalog failed.');

    component.preset.set('Risk');
    component.exporting.set(false);
    query.loading.set(false);
    query.error.set(null);
    component.loadError.set(null);
    await fixture.whenStable();
    expect(el.querySelector('[role=alert]')).toBeNull();
    expect(el.textContent).not.toContain('Loading');
    fixture.destroy();
  });
});

describe('Positions page', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.clear();
  });

  it('waits for the as-of date before the first request, so the first view loads once', async () => {
    const { fixture, http, grid, query, ready } = await render({ scope: 'pending' });
    await ready();
    expect(grid.setGridOption).not.toHaveBeenCalledWith('datasource', expect.anything());
    http.expectOne('/api/meta/as-of').flush({ latest: '2026-10-06', dates: ['2026-10-06'] });
    http.expectOne('/api/meta/portfolios').flush([]);
    await fixture.whenStable();
    expect(query.view.asOf).toBe('2026-10-06');
    expect(grid.setGridOption).toHaveBeenCalledWith('datasource', query.datasource);
    expect(grid.setGridOption.mock.calls.filter((c) => c[0] === 'datasource').length).toBe(1);
  });

  it('still opens the grid when the as-of dates cannot load (the API defaults to the latest)', async () => {
    const { grid, query, ready } = await render({ scope: 'failed' });
    await ready();
    expect(grid.setGridOption).toHaveBeenCalledWith('datasource', query.datasource);
    expect(query.view.asOf).toBeNull();
    Object.assign(URL, { createObjectURL: () => 'blob:z', revokeObjectURL: () => undefined });
    let name = '';
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { name = this.download; });
    TestBed.inject(KeyboardService).actions().exportCsv!();
    TestBed.inject(HttpTestingController).expectOne('/api/positions/export').flush(new Blob(['a']));
    expect(name).toBe('positions-latest.csv');
  });

  it('loads catalog and presets, then opens Risk with the identity columns pinned and only displayed columns requested', async () => {
    const { el, grid, stub, query, ready } = await render();
    expect(el.querySelector('.skeleton')).toBeNull();
    expect(stub).toBeTruthy();
    expect([...el.querySelectorAll('[data-testid=preset] option')].map((o) => o.textContent)).toEqual(['Risk', 'Mine (mine)']);
    await ready();
    const applied = grid.applyColumnState.mock.calls[0][0];
    expect(applied.state.slice(0, 3)).toEqual(['deal_name', 'class', 'cusip'].map((colId) => ({ colId, hide: false, pinned: 'left' })));
    expect(applied.defaultState).toEqual({ hide: true });
    expect(query.view.columns).toEqual(['deal_name', 'dv01']);
    expect(grid.setGridOption).toHaveBeenCalledWith('datasource', query.datasource);
  });

  it('opens the remembered preset with its saved filters', async () => {
    const { grid, ready, el } = await render({ remembered: 'Mine' });
    await ready();
    expect(grid.setFilterModel).toHaveBeenCalledWith({ dv01: { filterType: 'number' } });
    expect(el.textContent).toContain('Delete');
  });

  it('shows an error tile when the catalog cannot load', async () => {
    const { el } = await render({ failLoad: true });
    expect(el.querySelector('[role=alert]')?.textContent).toContain('Could not load the column catalog');
    expect(el.querySelector('.skeleton')).toBeNull();
  });

  it('re-requests only when the displayed set changes, and purges on a new view', async () => {
    const { stub, grid, query, ready } = await render();
    stub!.displayedColumnsChanged.emit({}); // before the grid is ready: ignored
    await ready();
    const setColumns = vi.spyOn(query, 'setColumns');
    grid.getAllDisplayedColumns.mockReturnValue(['deal_name'].map((id) => ({ getColId: () => id })));
    stub!.displayedColumnsChanged.emit({});
    expect(setColumns).toHaveBeenCalledWith(['deal_name']);
    expect(grid.purgeInfiniteCache).toHaveBeenCalled();
  });

  it('marks first rows painted once and reports visible rows', async () => {
    const { stub, grid, ready } = await render();
    const mark = vi.spyOn(performance, 'mark');
    stub!.viewportChanged.emit({}); // no grid yet
    await ready();
    stub!.firstDataRendered.emit({});
    stub!.firstDataRendered.emit({});
    expect(mark).toHaveBeenCalledTimes(1);
    expect(mark).toHaveBeenCalledWith(firstRowsMark);
    stub!.bodyScrollEnd.emit({});
    expect(TestBed.inject(StatusService).visibleRows()).toBe(40);
    grid.getLastDisplayedRowIndex.mockReturnValue(-1);
    stub!.viewportChanged.emit({});
    expect(TestBed.inject(StatusService).visibleRows()).toBe(0);
  });

  it('feeds the quick filter, switches presets and ignores unknown ones', async () => {
    const { el, grid, query, ready } = await render();
    const typed = vi.spyOn(query, 'type');
    const quick = el.querySelector('[data-testid=quick-filter]') as HTMLInputElement;
    quick.value = 'clo';
    quick.dispatchEvent(new Event('input'));
    expect(typed).toHaveBeenCalledWith('clo');

    const select = el.querySelector('[data-testid=preset]') as HTMLSelectElement;
    select.value = 'Mine';
    select.dispatchEvent(new Event('change')); // before ready: nothing to apply to
    expect(grid.applyColumnState).not.toHaveBeenCalled();
    await ready();
    select.dispatchEvent(new Event('change'));
    expect(localStorage.getItem('desk.positions.preset')).toBe('Mine');
    grid.applyColumnState.mockClear();
    TestBed.inject(KeyboardService).actions().nextPreset!();
    expect(grid.applyColumnState).toHaveBeenCalled();
  });

  it('follows the scope, fills the summary row and status bar, and repaints on a new negative style', async () => {
    const { fixture, grid, query, ready } = await render();
    const update = vi.spyOn(query, 'update');
    await ready();
    TestBed.inject(ScopeService).asOf.set('2026-10-05');
    await fixture.whenStable();
    expect(update).toHaveBeenCalledWith({ asOf: '2026-10-05', portfolioIds: [] });
    update.mockClear();
    TestBed.inject(ThemeService).palette.set('colorblind'); // an unrelated change re-runs nothing here
    await fixture.whenStable();
    expect(update).not.toHaveBeenCalled();
    TestBed.inject(ScopeService).selected.set([3]);
    await fixture.whenStable();
    expect(update).toHaveBeenCalledWith({ asOf: '2026-10-05', portfolioIds: [3] });

    query.lastBlock.set({ columns: [], data: [], rowCount: 42, summary: { dv01: 9 }, asOf: 'x', generatedAt: 'x' });
    query.lastInfo.set({ ms: 5, cache: 'HIT', bytes: 10, serverMs: 1 });
    await fixture.whenStable();
    expect(grid.setGridOption).toHaveBeenCalledWith('pinnedBottomRowData', [{ position_id: -1, deal_name: 'Total', dv01: 9 }]);
    expect(TestBed.inject(StatusService).totalRows()).toBe(42);
    expect(TestBed.inject(StatusService).lastRequest()?.cache).toBe('HIT');

    TestBed.inject(ThemeService).negatives.set('parens');
    await fixture.whenStable();
    expect(grid.refreshCells).toHaveBeenCalledWith({ force: true });
  });

  it('shows loading and block errors from the query', async () => {
    const { fixture, el, query } = await render();
    query.loading.set(true);
    query.error.set('Could not load positions.');
    await fixture.whenStable();
    expect(el.textContent).toContain('Loading…');
    expect(el.querySelector('[role=alert]')?.textContent).toContain('Could not load positions');
  });

  it('saves the current state as a preset, and reports a refused name', async () => {
    const { fixture, el, http, ready } = await render();
    const prompt = vi.spyOn(window, 'prompt').mockReturnValueOnce(null).mockReturnValueOnce(' Desk view ').mockReturnValueOnce('Risk');
    const save = () => (el.querySelector('[aria-label="Save the current columns as a preset"]') as HTMLButtonElement).click();
    save(); // no grid yet
    expect(prompt).not.toHaveBeenCalled();
    await ready();
    save(); // cancelled
    http.expectNone('/api/presets/positions');

    save();
    const req = http.expectOne((r) => r.method === 'PUT');
    expect(req.request.body).toEqual({ name: 'Desk view', state: { columnState: [{ colId: 'dv01', hide: false }], filterModel: {} } });
    req.flush(null);
    await fixture.whenStable();
    expect(el.textContent).toContain('Desk view (mine)');
    expect(localStorage.getItem('desk.positions.preset')).toBe('Desk view');

    save();
    http.expectOne((r) => r.method === 'PUT').flush('no', { status: 400, statusText: 'x' });
    await fixture.whenStable();
    expect(el.querySelector('[role=alert]')?.textContent).toContain('Could not save "Risk"');
  });

  it('offers the current name when saving over an own preset, and deletes it', async () => {
    const { fixture, el, http, grid, ready } = await render({ remembered: 'Mine' });
    await ready();
    const prompt = vi.spyOn(window, 'prompt').mockReturnValue(null);
    (el.querySelector('[aria-label="Save the current columns as a preset"]') as HTMLButtonElement).click();
    expect(prompt.mock.calls[0][1]).toBe('Mine');

    (el.querySelector('[aria-label="Delete this preset"]') as HTMLButtonElement).click();
    http.expectOne((r) => r.method === 'DELETE').flush('no', { status: 500, statusText: 'x' });
    await fixture.whenStable();
    expect(el.querySelector('[role=alert]')?.textContent).toContain('Could not delete "Mine"');

    (el.querySelector('[aria-label="Delete this preset"]') as HTMLButtonElement).click();
    http.expectOne((r) => r.method === 'DELETE' && r.urlWithParams.endsWith('name=Mine')).flush(null);
    await fixture.whenStable();
    expect(el.textContent).not.toContain('Mine');
    expect(grid.applyColumnState).toHaveBeenCalledTimes(2); // reopened Risk
    expect(el.querySelector('[aria-label="Delete this preset"]')).toBeNull();
  });

  it('exports the current view as a CSV download, once at a time', async () => {
    const { fixture, el, http, query, grid, ready } = await render();
    await ready();
    const exportRequest = vi.spyOn(query, 'exportRequest').mockReturnValue({ columns: ['dv01'] });
    grid.getAllDisplayedColumns.mockReturnValue(['dv01', 'deal_name'].map((id) => ({ getColId: () => id }))); // dragged
    const createUrl = vi.fn(() => 'blob:x');
    const revoke = vi.fn();
    Object.assign(URL, { createObjectURL: createUrl, revokeObjectURL: revoke });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);

    const button = el.querySelector('[data-testid=export]') as HTMLButtonElement;
    button.click();
    TestBed.inject(KeyboardService).actions().exportCsv!(); // ignored while one is running
    await fixture.whenStable();
    expect(button.textContent).toContain('Exporting');
    http.expectOne('/api/positions/export').flush(new Blob(['a,b']));
    await fixture.whenStable();
    expect(exportRequest).toHaveBeenCalledWith(['dv01', 'deal_name']); // on-screen order, not the preset's
    expect(click).toHaveBeenCalled();
    expect(revoke).toHaveBeenCalledWith('blob:x');

    button.click();
    http.expectOne('/api/positions/export').flush(new Blob(['busy']), { status: 429, statusText: 'x' });
    await fixture.whenStable();
    expect(el.querySelector('[role=alert]')?.textContent).toContain('Export failed');
  });

  it('exports the query view when the grid is not ready yet, named after the as-of date', async () => {
    const { http, query } = await render();
    query.update({ asOf: '2026-10-05' });
    Object.assign(URL, { createObjectURL: () => 'blob:y', revokeObjectURL: () => undefined });
    let name = '';
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { name = this.download; });
    TestBed.inject(KeyboardService).actions().exportCsv!();
    http.expectOne('/api/positions/export').flush(new Blob(['a']));
    expect(name).toBe('positions-2026-10-05.csv');
  });

  it('copies the focused cell on Ctrl+C (Community has no range copy)', async () => {
    const { el, grid, stub, ready } = await render();
    const writeText = vi.fn(() => Promise.resolve());
    Object.assign(navigator, { clipboard: { writeText } });
    const gridEl = el.querySelector('ag-grid-angular')!;
    const press = (init: KeyboardEventInit) => gridEl.dispatchEvent(new KeyboardEvent('keydown', { ...init, bubbles: true }));

    press({ key: 'c', ctrlKey: true }); // before ready
    expect(writeText).not.toHaveBeenCalled();
    await ready();
    press({ key: 'c' });
    press({ key: 'v', ctrlKey: true });
    expect(writeText).not.toHaveBeenCalled();
    press({ key: 'c', metaKey: true });
    expect(writeText).toHaveBeenLastCalledWith('512.3');
    grid.getDisplayedRowAtIndex.mockReturnValue({ data: { dv01: null } });
    press({ key: 'C', ctrlKey: true });
    expect(writeText).toHaveBeenLastCalledWith('');
    grid.getDisplayedRowAtIndex.mockReturnValue(undefined);
    press({ key: 'c', ctrlKey: true });
    expect(writeText).toHaveBeenLastCalledWith('');
    // The pinned Total row has its own index space.
    const pinned = { data: { dv01: 9999 } };
    Object.assign(grid, { getPinnedBottomRow: vi.fn(() => pinned) });
    grid.getFocusedCell.mockReturnValue({ rowIndex: 0, rowPinned: 'bottom', column: { getColId: () => 'dv01' } });
    press({ key: 'c', ctrlKey: true });
    expect(writeText).toHaveBeenLastCalledWith('9999');
    grid.getFocusedCell.mockReturnValue(null);
    writeText.mockClear();
    press({ key: 'c', ctrlKey: true });
    expect(writeText).not.toHaveBeenCalled();
    expect(stub).toBeTruthy();
  });

  it('registers page shortcuts and clears them and the status on leave', async () => {
    const { fixture, el } = await render();
    const focus = vi.spyOn(el.querySelector('[data-testid=quick-filter]') as HTMLInputElement, 'focus');
    TestBed.inject(KeyboardService).actions().focusQuickFilter!();
    expect(focus).toHaveBeenCalled();
    TestBed.inject(StatusService).totalRows.set(3);
    fixture.destroy();
    expect(TestBed.inject(KeyboardService).actions()).toEqual({});
    expect(TestBed.inject(StatusService).totalRows()).toBeNull();
  });

  it('works without browser storage', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    const { ready, grid } = await render();
    await ready();
    expect(grid.applyColumnState).toHaveBeenCalled();
  });
});
