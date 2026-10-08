import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Positions } from './positions';

/**
 * Renders the real component (no overrides, so its compiled template runs) with the real AG Grid in jsdom.
 * jsdom has no layout engine: the grid renders its structure, which is all this test needs.
 */
describe('Positions page with the real grid', () => {
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
    fixture.destroy();
  });
});
