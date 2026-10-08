import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PositionsQuery } from './positions-query';
import { Positions } from './positions';

/**
 * Renders the real component with its compiled (AOT) template and the real AG Grid. Kept apart from
 * positions.spec.ts, whose TestBed.overrideComponent recompiles Positions in JIT for the rest of that module;
 * with isolate: true (angular.json) this file gets its own module graph, so test order can't hide these branches.
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
