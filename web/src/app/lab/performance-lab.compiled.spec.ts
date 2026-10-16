import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CatalogColumn, Preset } from '../data-access/api.types';
import { LabClient, ResourceTimings } from './lab-client';
import { PerformanceLab } from './performance-lab';

/**
 * Renders the real component with its compiled (AOT) template, its own providers and the real AG Grid. Kept apart
 * from performance-lab.spec.ts, whose TestBed.overrideComponent recompiles PerformanceLab in JIT for the rest of
 * that module (as positions.compiled.spec.ts does for Positions).
 */
const catalog: CatalogColumn[] = [
  { name: 'position_id', group: 'Keys', kind: 'Key', aggregation: 'None', header: 'Position' },
  { name: 'deal_name', group: 'Keys', kind: 'Text', aggregation: 'None', header: 'Deal' },
  { name: 'market_value', group: 'Risk', kind: 'Money', aggregation: 'Sum', header: 'MV' },
];
const presets: Preset[] = [{ name: 'Risk', builtIn: true, state: { columns: ['deal_name', 'market_value'] }, updatedAt: null }];
const json = (doc: unknown) => new TextEncoder().encode(JSON.stringify(doc)).buffer as ArrayBuffer;
const columnar = { columns: ['position_id', 'deal_name', 'market_value'], data: [[1, 2], ['A', 'B'], [1.5, 2.5]], rowCount: 2, summary: {} };
const timing = { 'Server-Timing': 'db;dur=8.2, ser;dur=0.4, total;dur=9.1' };

async function until<T>(fn: () => T | undefined | null | false, ms = 3000): Promise<T> {
  const end = Date.now() + ms;
  for (;;) {
    const v = fn();
    if (v) return v;
    if (Date.now() > end) throw new Error('timed out waiting');
    await new Promise((r) => setTimeout(r, 5));
  }
}

beforeAll(() => {
  globalThis.ResizeObserver ??= class {
    observe(): void { /* jsdom: no layout */ }
    unobserve(): void { /* jsdom: no layout */ }
    disconnect(): void { /* jsdom: no layout */ }
  } as unknown as typeof ResizeObserver;
});

describe('Performance Lab as compiled', () => {
  it('runs end to end with its own timing observer, client and the real grid', async () => {
    TestBed.configureTestingModule({ imports: [PerformanceLab], providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(PerformanceLab);
    const http = TestBed.inject(HttpTestingController);
    const el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
    expect(fixture.debugElement.injector.get(LabClient)).toBeInstanceOf(LabClient);
    expect(fixture.debugElement.injector.get(ResourceTimings)).toBeInstanceOf(ResourceTimings);
    http.expectOne('/api/meta/columns').flush(catalog);
    http.expectOne('/api/presets/positions').flush(presets);
    http.expectOne('/api/meta/as-of').flush({ latest: '2026-10-06', dates: ['2026-10-06'] });
    http.expectOne('/api/meta/portfolios').flush([]);
    await fixture.whenStable();
    el.querySelector<HTMLButtonElement>('[data-testid=run]')!.click();
    const req = await until(() => http.match((r) => r.url.startsWith('/api/positions/query?lab='))[0]);
    req.flush(json(columnar), { headers: { ...timing, 'X-Cache': 'MISS' } });
    // jsdom reports no resource timing: the run waits for it, then shows n/a.
    const row = await until(() => el.querySelector('[data-testid=run-1]'), 3000);
    await fixture.whenStable();
    const cells = [...row.querySelectorAll('td')];
    expect(cells.map((td) => td.classList.contains('text'))).toEqual(cells.map((_, i) => [1, 6, 13].includes(i)));
    expect(cells[4].textContent).toBe('n/a');

    // Every other block of the template: copied, the copy fallback, P3's toggles and an error.
    const click = async (id: string) => { el.querySelector<HTMLButtonElement>(`[data-testid=${id}]`)!.click(); await fixture.whenStable(); };
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: vi.fn().mockResolvedValue(undefined) }, configurable: true });
    await click('copy');
    await until(() => el.querySelector('[data-testid=copied]'));
    Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });
    await click('copy');
    await until(() => el.querySelector('[data-testid=markdown]'));
    await click('target-p3');
    expect(el.querySelector('[data-testid=p3-naive]')).toBeTruthy();
    await click('run');
    const calls = await until(() => { const m = http.match((r) => r.url.startsWith('/api/insights/')); return m.length ? m : null; });
    calls.forEach((r) => r.flush(null, { status: 503, statusText: 'Unavailable' }));
    await until(() => el.querySelector('[data-testid=error]'));
  });
});
