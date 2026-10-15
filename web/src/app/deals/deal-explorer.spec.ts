import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { Signal, WritableSignal } from '@angular/core';
import type { GridApi, GridReadyEvent, ModelUpdatedEvent, RowClickedEvent } from 'ag-grid-community';
import { firstValueFrom, throwError, toArray } from 'rxjs';
import { KeyboardService } from '../core/keyboard.service';
import { ScopeService } from '../core/scope.service';
import { StatusService } from '../core/status.service';
import { DealDetail, DealList, DealRow } from '../data-access/api.types';
import { DealExplorer, loaded } from './deal-explorer';

const row = (dealId: number, bonds: number, held: number | null): DealRow => ({
  dealId, name: `CLO 2024-${dealId}`, sector: 'CLO', subSector: 'BSL', vintage: 2024, status: 'Active', originalBalance: 500_000_000,
  bondCount: bonds, currentBalance: bonds ? 400_000_000 : null,
  repBondId: bonds ? dealId * 10 : null, repClass: bonds ? 'A1' : null, repCusip: bonds ? '12345ABC1' : null,
  repCurrentBalance: bonds ? 250_000_000 : null, repCouponType: bonds ? 'FLOAT' : null, repCoupon: bonds ? 0.014 : null, repRating: bonds ? 'AAA' : null,
  positions: held === null ? null : 2, heldFace: held, heldMarketValue: held,
});

const list: DealList = { asOf: '2026-10-06', deals: [row(1, 3, 1_500_000), row(2, 0, null), row(3, 1, null)] };

const detail = (dealId: number, bonds: boolean): DealDetail => ({
  asOf: '2026-10-06',
  deal: { dealId, name: `CLO 2024-${dealId}`, sector: 'CLO', subSector: 'BSL', vintage: 2024, closingDate: '2024-03-01', collateralType: 'Leveraged loans', originalBalance: 500_000_000, currency: 'USD', status: bonds ? 'Active' : 'Announced' },
  tranches: bonds ? [
    { bondId: 10, class: 'A1', cusip: '12345ABC1', seniorityRank: 1, originalBalance: 300e6, currentBalance: 300e6, factor: 1, couponType: 'FLOAT', couponOrMargin: 0.014, couponIndex: 'SOFR', ratingSp: 'AAA', ratingMoodys: 'Aaa', ratingFitch: 'AAA', legalFinal: '2037-04-15', expectedMaturity: '2031-04-15' },
    { bondId: 11, class: 'E', cusip: '12345ABD9', seniorityRank: 5, originalBalance: 100e6, currentBalance: 100e6, factor: 1, couponType: 'RESIDUAL', couponOrMargin: null, couponIndex: null, ratingSp: null, ratingMoodys: null, ratingFitch: null, legalFinal: '2037-04-15', expectedMaturity: '2031-04-15' },
  ] : [],
  surveillance: bonds ? [
    { period: '2026-08-31', loanCount: 400, dq30: 0.02, dq60: 0.01, dq90Plus: 0.005, cpr: 0.1, cdr: 0.01, severity: 0.3, ocCushion: 0.04, icCushion: 0.1 },
    { period: '2026-09-30', loanCount: 398, dq30: 0.021, dq60: 0.011, dq90Plus: 0.006, cpr: 0.09, cdr: 0.012, severity: 0.31, ocCushion: -0.01, icCushion: 0.09 },
  ] : [],
  positions: bonds ? [
    { positionId: 7, portfolioId: 1, portfolio: 'Core Credit', bondId: 10, class: 'A1', face: 1_000_000, marketValue: 990_000, price: 99 },
    { positionId: 8, portfolioId: 1, portfolio: 'Core Credit', bondId: 11, class: 'E', face: 500_000, marketValue: 510_000, price: 102 },
    { positionId: 9, portfolioId: 1, portfolio: 'Core Credit', bondId: 11, class: 'E', face: 0, marketValue: null, price: null },
  ] : [],
});

interface Internals {
  selectedId: WritableSignal<number | null>;
  quickText: Signal<string>;
  select(e: RowClickedEvent<DealRow>): void;
  onListReady(e: GridReadyEvent): void;
  onModelUpdated(e: ModelUpdatedEvent): void;
}

/** A stand-in grid API: jsdom's real grid may or may not be ready by now, so the tests drive both states themselves. */
const fakeGrid = (displayed: number) => {
  let destroyed = false;
  const api = { getDisplayedRowCount: vi.fn(() => displayed), redrawRows: vi.fn(), isDestroyed: () => destroyed };
  return { api, destroy: () => { destroyed = true; }, event: { api } as unknown as GridReadyEvent & ModelUpdatedEvent & { api: GridApi } };
};

describe('Deal Explorer page (README §6 P4)', () => {
  beforeAll(() => {
    globalThis.ResizeObserver ??= class {
      observe(): void { /* jsdom: no layout */ }
      unobserve(): void { /* jsdom: no layout */ }
      disconnect(): void { /* jsdom: no layout */ }
    } as unknown as typeof ResizeObserver;
  });

  async function render() {
    TestBed.configureTestingModule({ imports: [DealExplorer], providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(DealExplorer);
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector(`[data-testid=${id}]`);
    const page = fixture.componentInstance as unknown as Internals;
    const listReq = (): TestRequest => http.expectOne((r) => r.url === '/api/deals');
    const dealReq = (id: number): TestRequest => http.expectOne((r) => r.url === `/api/deals/${id}`);
    const settle = () => fixture.whenStable();
    return { fixture, http, el, q, page, listReq, dealReq, settle };
  }

  function loadScope(http: HttpTestingController) {
    http.expectOne('/api/meta/as-of').flush({ latest: '2026-10-06', dates: ['2026-10-06', '2026-10-05'] });
    http.expectOne('/api/meta/portfolios').flush([{ portfolioId: 1, name: 'Core Credit', fundId: 1, fundName: 'Fund One' }]);
  }

  it('lists every deal once the scope is known, counting the zero-bond ones', async () => {
    const { http, el, q, listReq, settle } = await render();
    expect(el.querySelector('.skeleton')).not.toBeNull();
    loadScope(http);
    await settle();
    const req = listReq();
    expect(req.request.params.get('asOf')).toBe('2026-10-06');
    expect(req.request.params.has('portfolioIds')).toBe(false);
    req.flush(list);
    await settle();
    expect(q('deal-caption')?.textContent).toContain('3 deals · 1 without bonds yet · 1 held in scope');
    expect(q('deal-grid')).not.toBeNull();
    expect(q('deal-detail')?.textContent).toContain('Select a deal');
    expect(TestBed.inject(StatusService).totalRows()).toBe(3);
  });

  it('selecting a deal loads its capital structure, trend and positions', async () => {
    const { http, el, q, page, listReq, dealReq, settle } = await render();
    loadScope(http);
    await settle();
    listReq().flush(list);
    await settle();
    page.select({ data: undefined } as RowClickedEvent<DealRow>); // a row without data selects nothing
    await settle();
    expect(q('deal-detail')?.textContent).toContain('Select a deal');
    page.select({ data: list.deals[0] } as RowClickedEvent<DealRow>);
    await settle();
    expect(q('deal-detail')?.querySelector('.skeleton')).not.toBeNull();
    dealReq(1).flush(detail(1, true));
    await settle();

    expect(q('deal-name')?.textContent).toBe('CLO 2024-1');
    const bars = [...el.querySelectorAll('[data-testid=capital-stack] li')];
    expect(bars.map((b) => b.querySelector('.label')?.textContent)).toEqual(['A1', 'E']);
    expect(bars[0].querySelector('.share')?.textContent).toBe('75.00%');
    expect((bars[0].querySelector('.bar') as HTMLElement).style.width).toBe('75%');
    expect(q('tranche-grid')).not.toBeNull();
    const table = q('trend-table')!;
    expect([...table.querySelectorAll('thead th')].map((th) => th.textContent?.trim())).toEqual(['', 'Aug 26', 'Sep 26']);
    expect(table.querySelector('td.neg')?.textContent?.trim()).toBe('-1.00%'); // the negative OC cushion
    expect(q('position-grid')).not.toBeNull();
    expect(q('held-mv')?.textContent).toBe('1,500,000');
  });

  it('a zero-bond deal says so instead of drawing an empty structure', async () => {
    const { http, q, page, listReq, dealReq, settle } = await render();
    loadScope(http);
    await settle();
    listReq().flush(list);
    page.selectedId.set(2);
    await settle();
    dealReq(2).flush(detail(2, false));
    await settle();
    expect(q('no-tranches')).not.toBeNull();
    expect(q('capital-stack')).toBeNull();
    expect(q('no-positions')).not.toBeNull();
    expect(q('held-mv')?.textContent).toBe('—');
  });

  it('a new selection cancels the deal in flight, so a stale deal never paints', async () => {
    const { http, q, page, listReq, dealReq, settle } = await render();
    loadScope(http);
    await settle();
    listReq().flush(list);
    page.selectedId.set(1);
    await settle();
    const first = dealReq(1);
    page.selectedId.set(3);
    await settle();
    expect(first.cancelled).toBe(true);
    dealReq(3).flush(detail(3, true));
    await settle();
    expect(q('deal-name')?.textContent).toBe('CLO 2024-3');
  });

  it('a scope change re-requests the list and the open deal with the chosen portfolios', async () => {
    const { http, page, listReq, dealReq, settle } = await render();
    loadScope(http);
    await settle();
    listReq().flush(list);
    page.selectedId.set(1);
    await settle();
    dealReq(1).flush(detail(1, true));
    TestBed.inject(ScopeService).toggle(1);
    await settle();
    expect(listReq().request.params.get('portfolioIds')).toBe('1');
    expect(dealReq(1).request.params.get('portfolioIds')).toBe('1');
  });

  it('a failed list or deal shows an error tile, and retry asks again', async () => {
    const { http, q, page, listReq, dealReq, settle } = await render();
    loadScope(http);
    await settle();
    listReq().flush(null, { status: 500, statusText: 'boom' });
    await settle();
    const alert = q('deal-grid')?.parentElement?.querySelector('[role=alert]') ?? document.querySelector('[role=alert]');
    expect(alert?.textContent).toContain('Could not load deals');
    (alert!.querySelector('button') as HTMLButtonElement).click();
    await settle();
    listReq().flush(list);
    page.selectedId.set(1);
    await settle();
    dealReq(1).flush(null, { status: 503, statusText: 'down' });
    await settle();
    expect(q('deal-detail')?.querySelector('[role=alert]')?.textContent).toContain('Could not load this deal');
  });

  it('feeds the quick filter through the debounce, keeps the status bar in step, and registers the / shortcut', async () => {
    const { http, q, page, listReq, settle } = await render();
    loadScope(http);
    await settle();
    listReq().flush(list);
    await settle();
    const input = q('quick-filter') as HTMLInputElement;
    input.value = 'CLO';
    input.dispatchEvent(new Event('input'));
    await settle();
    await vi.waitFor(() => expect(page.quickText()).toBe('CLO'), { timeout: 2000, interval: 20 });
    page.onModelUpdated(fakeGrid(1).event);
    expect(TestBed.inject(StatusService).visibleRows()).toBe(1);
    expect(TestBed.inject(KeyboardService).actions().focusQuickFilter).toBeTypeOf('function');
    TestBed.inject(KeyboardService).actions().focusQuickFilter!();
    expect(document.activeElement).toBe(input);
  });

  it('reads counts and redraws only through a live list grid, never a destroyed one', async () => {
    const { http, page, listReq, dealReq, settle } = await render();
    const status = TestBed.inject(StatusService);
    // jsdom's real grid reports in on its own schedule; ignore it, so only the stand-ins below drive the page.
    const ready = page.onListReady.bind(page);
    page.onListReady = () => undefined;
    page.onModelUpdated = () => undefined;
    loadScope(http);
    await settle();
    listReq().flush(list);
    await settle();

    const first = fakeGrid(2); // the quick filter leaves 2 of 3 rows
    ready(first.event);
    expect(status.visibleRows()).toBe(2);
    page.selectedId.set(1);
    await settle();
    expect(first.api.redrawRows).toHaveBeenCalledTimes(1);
    dealReq(1);

    // A scope change rebuilds the grid: the old API is destroyed, and the status bar no longer asks it.
    first.destroy();
    TestBed.inject(ScopeService).toggle(1);
    await settle();
    expect(status.visibleRows()).toBe(0);
    expect(status.totalRows()).toBe(0);
    page.selectedId.set(3);
    await settle();
    expect(first.api.redrawRows).toHaveBeenCalledTimes(1);
    expect(first.api.getDisplayedRowCount).toHaveBeenCalledTimes(1);
    listReq().flush(list);
    await settle();
    expect(status.visibleRows()).toBe(3);

    // While a live grid is there, a list change counts what the grid displays.
    const second = fakeGrid(1);
    ready(second.event);
    TestBed.inject(ScopeService).toggle(1);
    await settle();
    expect(second.api.getDisplayedRowCount).toHaveBeenCalledTimes(2);
    expect(status.visibleRows()).toBe(1);
    http.match(() => true).filter((r) => !r.cancelled).forEach((r) => r.flush(r.request.url === '/api/deals' ? list : detail(3, true)));
  });

  it('retrying a failed deal asks for the deal only, and keeps the list grid', async () => {
    const { http, q, page, listReq, dealReq, settle } = await render();
    loadScope(http);
    await settle();
    listReq().flush(list);
    page.selectedId.set(1);
    await settle();
    dealReq(1).flush(null, { status: 503, statusText: 'down' });
    await settle();
    (q('deal-detail')!.querySelector('[role=alert] button') as HTMLButtonElement).click();
    await settle();
    http.expectNone((r) => r.url === '/api/deals');
    dealReq(1).flush(detail(1, true));
    await settle();
    expect(q('deal-name')?.textContent).toBe('CLO 2024-1');
    expect(q('deal-grid')).not.toBeNull();
  });

  it('shows the held MV from the list row, and a dash when the deal is not in the list', async () => {
    const { http, q, page, listReq, dealReq, settle } = await render();
    loadScope(http);
    await settle();
    listReq().flush({ ...list, deals: [row(1, 3, 2_250_000)] });
    page.selectedId.set(1);
    await settle();
    dealReq(1).flush(detail(1, true)); // positions sum to 1,500,000; the server's decimal total wins
    await settle();
    expect(q('held-mv')?.textContent).toBe('2,250,000');
    page.selectedId.set(4);
    await settle();
    dealReq(4).flush(detail(4, true));
    await settle();
    expect(q('held-mv')?.textContent).toBe('—');
  });

  it('turns a request into loading, then ready or error', async () => {
    expect(await firstValueFrom(loaded(throwError(() => new Error('x'))).pipe(toArray()))).toEqual([{ kind: 'loading' }, { kind: 'error' }]);
  });
});
