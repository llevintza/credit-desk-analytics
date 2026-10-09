import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { FundPerformance } from '../data-access/api.types';
import { FundPerformancePage } from './fund-performance';

const perf = (range: string, months: string[]): FundPerformance => ({
  fundId: 1, fundName: 'Structured Credit Opportunities Fund', range, from: months[0] ?? null, to: months.at(-1) ?? null, months,
  rows: [
    { label: 'Balance', format: 'money0', values: months.map((_, i) => 100_000_000 + i) },
    { label: 'IRR', format: 'pct2', values: months.map(() => 0.05) },
  ],
});

describe('Fund performance page (README §6 P2)', () => {
  beforeAll(() => {
    globalThis.ResizeObserver ??= class {
      observe(): void { /* jsdom: no layout */ }
      unobserve(): void { /* jsdom: no layout */ }
      disconnect(): void { /* jsdom: no layout */ }
    } as unknown as typeof ResizeObserver;
  });

  async function render() {
    TestBed.configureTestingModule({ imports: [FundPerformancePage], providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(FundPerformancePage);
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector(`[data-testid=${id}]`);
    const perfReq = (fundId: number): TestRequest => http.expectOne((r) => r.url === `/api/funds/${fundId}/performance`);
    return { fixture, http, el, q, perfReq };
  }

  function loadScope(http: HttpTestingController) {
    http.expectOne('/api/meta/as-of').flush({ latest: '2026-10-06', dates: ['2026-10-06'] });
    http.expectOne('/api/meta/portfolios').flush([
      { portfolioId: 1, name: 'P1', fundId: 1, fundName: 'Structured Credit Opportunities Fund' },
      { portfolioId: 2, name: 'P2', fundId: 4, fundName: 'Residential Credit Fund' },
    ]);
  }

  it('opens the first fund on YTD and builds a column per month from the response', async () => {
    const { fixture, http, el, q, perfReq } = await render();
    expect(q('fund-grid')).toBeNull();
    loadScope(http);
    await fixture.whenStable();
    const req = perfReq(1);
    expect(req.request.params.get('range')).toBe('YTD');
    expect(el.querySelector('.skeleton')).not.toBeNull();
    req.flush(perf('YTD', ['2026-01-31', '2026-02-28', '2026-03-31']));
    await fixture.whenStable();
    expect(q('caption')?.textContent).toContain('3 months');
    expect(q('fund-grid')).not.toBeNull();
    expect(el.querySelector('.skeleton')).toBeNull();
    expect(q('range-YTD')?.getAttribute('aria-pressed')).toBe('true');
    expect(q('range-1Y')?.textContent).toContain('Last 12M');
  });

  it('flipping ranges quickly cancels the stale request: only the last range is shown', async () => {
    const { fixture, http, q, perfReq } = await render();
    loadScope(http);
    await fixture.whenStable();
    const ytd = perfReq(1);
    (q('range-QTD') as HTMLButtonElement).click();
    await fixture.whenStable();
    const qtd = perfReq(1);
    (q('range-ITD') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(ytd.cancelled).toBe(true);
    expect(qtd.cancelled).toBe(true);
    perfReq(1).flush(perf('ITD', ['2021-01-31']));
    await fixture.whenStable();
    expect(q('caption')?.textContent).toContain('ITD · 1 month');
  });

  it('asks for both months before a custom request, and shows an empty range plainly', async () => {
    const { fixture, http, q, perfReq } = await render();
    loadScope(http);
    await fixture.whenStable();
    perfReq(1).flush(perf('YTD', ['2026-01-31']));
    (q('range-CUSTOM') as HTMLButtonElement).click();
    await fixture.whenStable();
    http.expectNone((r) => r.url.includes('/performance'));
    // The YTD data must not stay on screen under the CUSTOM selection.
    expect(q('fund-grid')).toBeNull();
    expect(q('incomplete')?.textContent).toContain('Pick a from and a to month');
    const set = (id: string, value: string) => {
      const input = q(id) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('change'));
    };
    set('from', '2001-01');
    await fixture.whenStable();
    http.expectNone((r) => r.url.includes('/performance'));
    set('to', '2001-06');
    await fixture.whenStable();
    const req = perfReq(1);
    expect([req.request.params.get('range'), req.request.params.get('from'), req.request.params.get('to')]).toEqual(['CUSTOM', '2001-01-01', '2001-06-01']);
    req.flush(perf('CUSTOM', []));
    await fixture.whenStable();
    expect(q('caption')?.textContent?.replace(/\s+/g, ' ')).toContain('0 months — no data in this range');

    // Leaving CUSTOM and coming back shows the months that are in effect, not blank pickers.
    (q('range-YTD') as HTMLButtonElement).click();
    await fixture.whenStable();
    perfReq(1).flush(perf('YTD', ['2026-01-31']));
    (q('range-CUSTOM') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect((q('from') as HTMLInputElement).value).toBe('2001-01');
    expect((q('to') as HTMLInputElement).value).toBe('2001-06');
    perfReq(1).flush(perf('CUSTOM', []));
  });

  it('repaints numbers when the negative style changes', async () => {
    const { fixture, http, perfReq } = await render();
    loadScope(http);
    await fixture.whenStable();
    perfReq(1).flush(perf('YTD', ['2026-01-31']));
    await fixture.whenStable();
    const view = (fixture.componentInstance as unknown as { view: () => { columns: unknown[] } | null }).view;
    const before = view()!.columns;
    TestBed.inject((await import('../core/theme.service')).ThemeService).negatives.set('parens');
    await fixture.whenStable();
    expect(view()!.columns).not.toBe(before);
  });

  it('switches fund, and shows an error tile when a request fails', async () => {
    const { fixture, http, el, q, perfReq } = await render();
    loadScope(http);
    await fixture.whenStable();
    perfReq(1).flush(perf('YTD', ['2026-01-31']));
    const select = q('fund') as HTMLSelectElement;
    select.value = '4';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    perfReq(4).flush('boom', { status: 500, statusText: 'x' });
    await fixture.whenStable();
    expect(el.querySelector('[role=alert]')?.textContent).toContain('Could not load fund performance');
    expect(q('fund-grid')).toBeNull();
  });
});
