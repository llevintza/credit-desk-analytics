import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService } from '../core/auth.service';
import { ScopeService } from '../core/scope.service';
import { ThemeService } from '../core/theme.service';
import { InsightGrid, InsightSource, InsightsResult } from '../data-access/api.types';
import { InsightsBoard } from './insights-board';
import { debounceMs, insightSources } from './source-stream';

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

const grid = (id: string, rows: (string | number | null)[][] = [['CLO', 1_500_000_000, -0.0123]]): InsightGrid => ({
  id, title: `Title ${id}`, columns: ['Sector', 'MV', 'Chg'], rows, format: { Sector: 'text', MV: 'money0', Chg: 'pct1' },
});
const result = (source: InsightSource): InsightsResult => ({
  source, asOf: '2026-10-06', grids: insightSources.find((s) => s.source === source)!.grids.map((id) => grid(id)),
});

describe('Insights board (README §6 P3)', () => {
  async function render() {
    TestBed.configureTestingModule({ imports: [InsightsBoard], providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(InsightsBoard);
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    const el = fixture.nativeElement as HTMLElement;
    const tiles = (source: InsightSource) => [...el.querySelectorAll<HTMLElement>(`[data-source=${source}]`)];
    const states = (source: InsightSource) => tiles(source).map((t) => t.dataset['state']);
    const pending = (source: InsightSource): TestRequest[] => http.match((r) => r.url === `/api/insights/${source}`);
    // Effects first (they arm the debounce timer), then past the debounce, then the render.
    const settle = async () => { await fixture.whenStable(); await wait(debounceMs * 2); await fixture.whenStable(); };
    http.expectOne('/api/meta/as-of').flush({ latest: '2026-10-06', dates: ['2026-10-06', '2026-10-05'] });
    http.expectOne('/api/meta/portfolios').flush([{ portfolioId: 1, name: 'Book 01', fundId: 1, fundName: 'Fund' }]);
    await settle();
    return { fixture, http, el, tiles, states, pending, settle };
  }

  it('draws a tile per grid, five requests, and paints each source as it lands while a slow one shows skeletons', async () => {
    const { el, states, pending, settle, fixture } = await render();
    expect(el.querySelectorAll('.tile')).toHaveLength(20);
    const reqs = Object.fromEntries(insightSources.map(({ source }) => [source, pending(source)]));
    for (const { source } of insightSources) expect(reqs[source]).toHaveLength(1);
    expect(reqs['core'][0].request.params.get('asOf')).toBe('2026-10-06');

    // Everyone but surveillance answers: their tiles paint, surveillance's stay skeletons.
    for (const source of ['core', 'market', 'pricing', 'reference'] as const) reqs[source][0].flush(result(source));
    await fixture.whenStable();
    for (const source of ['core', 'market', 'pricing', 'reference'] as const) expect(states(source).every((s) => s === 'ready')).toBe(true);
    expect(states('surveillance')).toEqual(['loading', 'loading', 'loading', 'loading', 'loading']);
    expect(el.querySelectorAll('[data-state=loading] .skeleton')).toHaveLength(5);

    reqs['surveillance'][0].flush(result('surveillance'));
    await settle();
    expect(el.querySelectorAll('[data-state=ready]')).toHaveLength(20);
    const cell = el.querySelector('[data-testid=tile-core-mv_sector_rating] tbody td') as HTMLElement;
    expect(cell.textContent?.trim()).toBe('1.5B');
    expect(cell.title).toBe('1500000000');
    const neg = el.querySelector('[data-testid=tile-core-mv_sector_rating] tbody td.neg') as HTMLElement;
    expect(neg.textContent?.trim()).toBe('-1.2%');
    expect(el.querySelector('[data-testid=tile-core-mv_sector_rating] h3')?.textContent).toBe('Title mv_sector_rating');
  });

  it('a source returning 500 shows exactly its error tiles; retry asks again and the rest is untouched', async () => {
    const { el, states, pending, settle, fixture } = await render();
    for (const { source } of insightSources)
      if (source === 'pricing') pending(source)[0].flush('boom', { status: 500, statusText: 'Server Error' });
      else pending(source)[0].flush(result(source));
    await fixture.whenStable();
    expect(states('pricing')).toEqual(['error', 'error', 'error']);
    expect(el.querySelectorAll('[data-state=error]')).toHaveLength(3);
    expect(el.querySelectorAll('[data-state=ready]')).toHaveLength(17);

    (el.querySelector('[data-testid=retry-pricing]') as HTMLButtonElement).click();
    await settle();
    expect(pending('core')).toHaveLength(0); // only the failed source asks again
    pending('pricing')[0].flush(result('pricing'));
    await fixture.whenStable();
    expect(el.querySelectorAll('[data-state=ready]')).toHaveLength(20);
  });

  it('changing the as-of date cancels the calls in flight', async () => {
    const { pending, settle } = await render();
    const first = insightSources.map(({ source }) => pending(source)[0]);
    TestBed.inject(ScopeService).asOf.set('2026-10-05');
    await settle();
    expect(first.every((r) => r.cancelled)).toBe(true);
    for (const { source } of insightSources) expect(pending(source)[0].request.params.get('asOf')).toBe('2026-10-05');
  });

  it('a portfolio selection is sent and shown in the caption; an empty grid says so', async () => {
    const { el, pending, settle, fixture } = await render();
    for (const { source } of insightSources) pending(source)[0].flush(result(source));
    TestBed.inject(ScopeService).toggle(1);
    await settle();
    expect(el.querySelector('[data-testid=insights-caption]')?.textContent).toContain('1 portfolio');
    for (const { source } of insightSources) {
      const [req] = pending(source);
      expect(req.request.params.get('portfolioIds')).toBe('1');
      req.flush({ ...result(source), grids: result(source).grids.map((g) => ({ ...g, rows: [] })) });
    }
    await fixture.whenStable();
    expect(el.querySelectorAll('.empty')).toHaveLength(20);
  });

  it('naive mode (dev or admin) fires one request per grid', async () => {
    const { el, http, settle, fixture } = await render();
    http.match((r) => r.url.startsWith('/api/insights/')).forEach((r) => r.flush(result(r.request.url.split('/').at(-1) as InsightSource)));
    (el.querySelector('[data-testid=naive]') as HTMLInputElement).click();
    await settle();
    const reqs = http.match((r) => r.url.startsWith('/api/insights/'));
    expect(reqs).toHaveLength(20);
    expect(reqs.every((r) => r.request.params.has('grid'))).toBe(true);
    reqs.forEach((r) => r.flush({ ...result('core'), grids: [grid(r.request.params.get('grid')!)] }));
    await fixture.whenStable();
    expect(el.querySelectorAll('[data-state=ready]')).toHaveLength(20);
  });

  it('admins get the naive-mode toggle', async () => {
    const { el, fixture } = await render();
    TestBed.inject(AuthService).me.set({ email: 'a@example.com', roles: ['admin'], expiresAt: '2099-12-31' });
    await fixture.whenStable();
    expect(el.querySelector('[data-testid=naive]')).not.toBeNull();
  });

  it('marks when the inputs change and when each source has painted (README §10 P3 budget)', async () => {
    const { pending, fixture } = await render();
    expect(performance.getEntriesByName('insights:start')).toHaveLength(1);
    pending('market')[0].flush(result('market'));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(performance.getEntriesByName('insights:painted:market').length).toBeGreaterThan(0);
  });

  it('repaints negatives in the chosen style', async () => {
    const { el, pending, fixture } = await render();
    pending('core')[0].flush(result('core'));
    await fixture.whenStable();
    TestBed.inject(ThemeService).negatives.set('parens');
    await fixture.whenStable();
    expect((el.querySelector('[data-testid=tile-core-mv_sector_rating] tbody td.neg') as HTMLElement).textContent?.trim()).toBe('(1.2%)');
  });
});
