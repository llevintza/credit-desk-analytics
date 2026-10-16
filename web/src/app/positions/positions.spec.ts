import { Component, input, output } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpEventType, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AgGridAngular } from 'ag-grid-angular';
import { throwError } from 'rxjs';
import { KeyboardService } from '../core/keyboard.service';
import { ScopeService } from '../core/scope.service';
import { StatusService } from '../core/status.service';
import { ThemeService } from '../core/theme.service';
import { CatalogColumn, Preset } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';
import { MarksOverlay } from './marks-overlay';
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
    getRowNode: vi.fn<(id: string) => unknown>(() => undefined),
    getPinnedBottomRow: vi.fn((): unknown => ({ id: 'total' })),
    flashCells: vi.fn(),
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
    expect(applied.state.slice(0, 3)).toEqual([
      { colId: 'deal_name', hide: false, sort: null, pinned: 'left' }, // Risk lists deal_name, unsorted
      { colId: 'class', hide: false, pinned: 'left' },
      { colId: 'cusip', hide: false, pinned: 'left' },
    ]);
    expect(applied.defaultState).toEqual({ hide: true, sort: null }); // the previous sort never leaks into the next view
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
    expect(grid.setGridOption).toHaveBeenCalledWith('pinnedBottomRowData', [{ position_id: -1, deal_name: 'Total · avgs |MV|-wtd', dv01: 9 }]);
    expect(TestBed.inject(StatusService).totalRows()).toBe(42);
    expect(TestBed.inject(StatusService).lastRequest()?.cache).toBe('HIT');

    TestBed.inject(ThemeService).negatives.set('parens');
    await fixture.whenStable();
    expect(grid.refreshCells).toHaveBeenCalledWith({ force: true });
  });

  it('live marks: requests bond_id, puts each new mark on its rows, flashes them and the server totals, and stops off the latest as-of', async () => {
    const { fixture, el, http, grid, query, ready } = await render();
    await ready();
    grid.getAllDisplayedColumns.mockReturnValue(['deal_name', 'price'].map((id) => ({ getColId: () => id })));
    query.update({ columns: ['deal_name', 'price'] });
    const overlay = fixture.debugElement.injector.get(MarksOverlay);
    expect(overlay.totalsRequest()).toEqual(query.exportRequest()); // price is shown: totals are worth asking for
    const toggle = el.querySelector('[data-testid=live-marks]') as HTMLButtonElement;
    expect(toggle.getAttribute('aria-pressed')).toBe('false');

    toggle.click();
    await fixture.whenStable();
    expect(query.view.overlay).toBe(true);
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
    const stream = http.expectOne((r) => r.url === '/api/marks/stream');

    // A block loads: its rows carry bond_id and are remembered under it.
    const success = vi.fn();
    query.datasource.getRows({ startRow: 0, endRow: 200, sortModel: [], filterModel: {}, successCallback: success, failCallback: vi.fn() } as never);
    const block = http.expectOne('/api/positions/query');
    expect(block.request.body.columns).toEqual(['deal_name', 'price', 'bond_id']);
    block.flush({ columns: ['position_id', 'bond_id', 'price'], data: [[1, 2], [7, 8], [99, 98]], rowCount: 2, summary: { price: 98.5 }, asOf: 'x', generatedAt: 'x' });
    expect(success.mock.calls[0][0]).toEqual([{ position_id: 1, bond_id: 7, price: 99 }, { position_id: 2, bond_id: 8, price: 98 }]);

    // A mark for bond 7 lands on row 1 as a new row object, and its price cell flashes.
    const node = { data: { position_id: 1, bond_id: 7, price: 99 }, updateData: vi.fn() };
    grid.getRowNode.mockImplementation((id: string) => (id === '1' ? node : undefined));
    stream.event({ type: HttpEventType.DownloadProgress, loaded: 1, partialText:
      'event: hello\ndata: {"asOf":"2026-10-06","bonds":2,"tickMs":1000,"endsAt":"x","seq":5}\n\n' +
      'event: mark\ndata: {"bondId":7,"seq":5,"eventTs":"t","price":100.5,"spreadBp":200}\n\n' +
      'event: mark\ndata: {"bondId":9,"seq":5,"eventTs":"t","price":90,"spreadBp":null}\n\n' });
    await fixture.whenStable();
    expect(node.updateData).toHaveBeenCalledWith({ position_id: 1, bond_id: 7, price: 100.5 });
    expect(grid.flashCells).toHaveBeenCalledWith({ rowNodes: [node], columns: ['price'] });
    expect(el.querySelector('[data-testid=live-status]')?.textContent).toContain('Live · 2 bonds');

    // A block loaded after the mark shows it straight away.
    query.datasource.getRows({ startRow: 200, endRow: 400, sortModel: [], filterModel: {}, successCallback: success, failCallback: vi.fn() } as never);
    http.expectOne('/api/positions/query').flush({ columns: ['position_id', 'bond_id', 'price'], data: [[3], [7], [99]], rowCount: 3, summary: {}, asOf: 'x', generatedAt: 'x' });
    expect(success.mock.calls[1][0]).toEqual([{ position_id: 3, bond_id: 7, price: 100.5 }]);

    // Server totals replace the block's marked totals on the pinned row, and flash there.
    grid.flashCells.mockClear();
    overlay.totals.set({ asOf: '2026-10-06', seq: 30, eventTs: 't', rowCount: 2, summary: { price: 99.9 } });
    await fixture.whenStable();
    expect(grid.setGridOption).toHaveBeenLastCalledWith('pinnedBottomRowData', [expect.objectContaining({ price: 99.9 })]);
    expect(grid.flashCells).toHaveBeenCalledWith({ rowNodes: [{ id: 'total' }], columns: ['price'] });
    grid.flashCells.mockClear();
    query.lastBlock.set({ columns: [], data: [], rowCount: 2, summary: { price: 98.4 }, asOf: 'x', generatedAt: 'x' });
    await fixture.whenStable();
    expect(grid.flashCells).not.toHaveBeenCalled(); // a block load re-pins the same totals without a second flash

    // Without a marked column on screen there is nothing to flash, nor any totals to ask for.
    grid.flashCells.mockClear();
    grid.getAllDisplayedColumns.mockReturnValue(['deal_name'].map((id) => ({ getColId: () => id })));
    query.update({ columns: ['deal_name'] });
    expect(overlay.totalsRequest()).toBeNull();
    overlay.totals.set({ asOf: '2026-10-06', seq: 45, eventTs: 't', rowCount: 2, summary: { price: 99.8 } });
    await fixture.whenStable();
    expect(grid.flashCells).not.toHaveBeenCalled();

    // A portfolio change restarts the stream for the new scope; an older as-of turns the overlay off.
    TestBed.inject(ScopeService).selected.set([3]);
    await fixture.whenStable();
    expect(http.expectOne((r) => r.url === '/api/marks/stream').request.params.get('portfolioIds')).toBe('3');
    TestBed.inject(ScopeService).asOf.set('2026-10-05');
    await fixture.whenStable();
    expect(overlay.status()).toBe('off');
    expect(query.view.overlay).toBe(false);
    expect(toggle.disabled).toBe(true);
    expect(el.querySelector('[data-testid=live-status]')).toBeNull();
  });

  it('live marks: says what the overlay is doing, and skips rows it cannot place', async () => {
    const { fixture, el, http, grid, query, ready } = await render();
    const overlay = fixture.debugElement.injector.get(MarksOverlay);
    const rows = [{ position_id: 1, bond_id: 7, price: 99 }];
    expect(query.decorate(rows)).toBe(rows); // overlay off: blocks pass through untouched
    overlay.onMarks([{ bondId: 7, seq: 1, eventTs: 't', price: 1, spreadBp: null }]); // no grid yet: nothing to do
    await ready();
    (el.querySelector('[data-testid=live-marks]') as HTMLButtonElement).click();
    await fixture.whenStable();
    const stream = http.expectOne((r) => r.url === '/api/marks/stream');
    // A row without a bond id is shown as it is; one whose block was purged since is skipped.
    expect(query.decorate([{ position_id: 5, price: 90 }])).toEqual([{ position_id: 5, price: 90 }]);
    query.decorate([{ position_id: 6, bond_id: 7, price: 90 }]);
    stream.event({ type: HttpEventType.DownloadProgress, loaded: 1, partialText:
      'event: mark\ndata: {"bondId":7,"seq":5,"eventTs":"t","price":100.5,"spreadBp":200}\n\n' +
      'event: mark\ndata: {"bondId":8,"seq":5,"eventTs":"t","price":90,"spreadBp":null}\n\n' });
    expect(grid.getRowNode).toHaveBeenCalledWith('6');
    expect(grid.flashCells).not.toHaveBeenCalled();

    const label = () => el.querySelector('[data-testid=live-status]')?.textContent?.trim();
    for (const [status, text] of [['connecting', 'Connecting…'], ['paused', 'Paused (idle) · click to resume'], ['empty', 'No bonds to mark'], ['error', 'Live marks unavailable']] as const) {
      overlay.status.set(status);
      await fixture.whenStable();
      expect(label()).toBe(text);
    }
    overlay.status.set('off');
    await fixture.whenStable();
    expect(label()).toBeUndefined();
  });

  it('live marks: turning it off closes the stream and drops bond_id', async () => {
    const { fixture, el, http, query, ready } = await render();
    await ready();
    const toggle = el.querySelector('[data-testid=live-marks]') as HTMLButtonElement;
    toggle.click();
    await fixture.whenStable();
    const stream = http.expectOne((r) => r.url === '/api/marks/stream');
    toggle.click();
    await fixture.whenStable();
    expect(stream.cancelled).toBe(true);
    expect(query.view.overlay).toBe(false);
    expect(query.request(query.view, 0, 1).columns).not.toContain('bond_id');
    expect(fixture.debugElement.injector.get(MarksOverlay).status()).toBe('off');
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
    http.expectOne((r) => r.method === 'PUT').flush({ title: 'Invalid preset name' }, { status: 400, statusText: 'x' });
    await fixture.whenStable();
    expect(el.querySelector('[role=alert]')?.textContent).toContain('Could not save "Risk": Invalid preset name');
  });

  it('says why a save failed: the server\'s detail (a 409 keeps its title), else its title, else a generic line', async () => {
    const { fixture, el, http, ready } = await render();
    await ready();
    vi.spyOn(window, 'prompt').mockReturnValue('Desk view');
    const save = () => (el.querySelector('[aria-label="Save the current columns as a preset"]') as HTMLButtonElement).click();
    const alert = () => el.querySelector('[role=alert]')?.textContent?.trim();
    const cases: [body: object | string | null, status: number, shown: string][] = [
      [{ title: 'Preset changed concurrently', detail: 'Try again.' }, 409, 'Could not save "Desk view": Preset changed concurrently: Try again.'],
      [{ title: 'Preset changed concurrently' }, 409, 'Could not save "Desk view": Preset changed concurrently'],
      [{ detail: 'Someone else saved it first.' }, 409, 'Could not save "Desk view": Someone else saved it first.'],
      [{ title: 'Bad request', detail: 'Names must be 1–64 characters.' }, 400, 'Could not save "Desk view": Names must be 1–64 characters.'],
      [{ title: 'Too many requests', detail: '' }, 429, 'Could not save "Desk view": Too many requests'],
      [{ detail: 42 }, 400, 'Could not save "Desk view".'],
      ['plain text', 500, 'Could not save "Desk view".'],
      [null, 502, 'Could not save "Desk view".'],
    ];
    for (const [body, status, shown] of cases) {
      save();
      http.expectOne((r) => r.method === 'PUT').flush(body, { status, statusText: 'x' });
      await fixture.whenStable();
      expect(alert()).toBe(shown);
    }

    save();
    http.expectOne((r) => r.method === 'PUT').error(new ProgressEvent('error')); // network failure
    await fixture.whenStable();
    expect(alert()).toBe('Could not save "Desk view".');

    vi.spyOn(TestBed.inject(DeskApi), 'savePreset').mockReturnValue(throwError(() => new Error('not HTTP')));
    save();
    await fixture.whenStable();
    expect(alert()).toBe('Could not save "Desk view".');
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
