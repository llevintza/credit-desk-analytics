import { createGrid } from 'ag-grid-community';
import { Preset } from '../data-access/api.types';
import { registerGridModules } from './grid-setup';
import { columnStateOf, initialPreset, nextPreset, presetColumnState } from './presets';

const presets: Preset[] = [
  { name: 'Risk', builtIn: true, state: { columns: ['deal_name', 'dv01'] }, updatedAt: null },
  { name: 'All', builtIn: true, state: { columns: [] }, updatedAt: null },
  { name: 'Mine', builtIn: false, state: { columnState: [{ colId: 'cs01', hide: false, width: 90 }] }, updatedAt: 'x' },
];

describe('presets', () => {
  it('turns a built-in column list into visible column state, and passes saved state through', () => {
    expect(columnStateOf(presets[0].state)).toEqual([{ colId: 'deal_name', hide: false, sort: null }, { colId: 'dv01', hide: false, sort: null }]);
    expect(columnStateOf(presets[2].state)).toEqual([{ colId: 'cs01', hide: false, width: 90 }]);
    expect(columnStateOf({})).toEqual([]);
  });

  it('switching to a built-in preset clears the previous sort (real grid column state)', () => {
    globalThis.ResizeObserver ??= class {
      observe(): void { /* jsdom: no layout */ }
      unobserve(): void { /* jsdom: no layout */ }
      disconnect(): void { /* jsdom: no layout */ }
    } as unknown as typeof ResizeObserver;
    registerGridModules();
    const host = document.createElement('div');
    const api = createGrid<Record<string, unknown>>(host, {
      rowModelType: 'infinite',
      columnDefs: ['deal_name', 'class', 'cusip', 'dv01', 'cs01'].map((colId) => ({ colId, field: colId, sortable: true })),
    });
    try {
      api.applyColumnState({ state: [{ colId: 'dv01', sort: 'desc' }, { colId: 'cs01', sort: 'asc' }] });
      expect(api.getColumnState().filter((c) => c.sort).length).toBe(2);

      api.applyColumnState(presetColumnState(presets[0].state)); // Risk: deal_name, dv01
      const state = api.getColumnState();
      expect(state.filter((c) => c.sort)).toEqual([]);
      expect(state.filter((c) => !c.hide).map((c) => c.colId)).toEqual(['deal_name', 'class', 'cusip', 'dv01']);
      expect(state.find((c) => c.colId === 'deal_name')?.pinned).toBe('left');

      // A saved preset brings its own sort back.
      api.applyColumnState(presetColumnState({ columnState: [{ colId: 'cs01', hide: false, sort: 'asc' }] }));
      expect(api.getColumnState().filter((c) => c.sort).map((c) => [c.colId, c.sort])).toEqual([['cs01', 'asc']]);

      // ...including its sort, sort order and width on an identity column, which stays pinned.
      api.applyColumnState(presetColumnState({
        columnState: [{ colId: 'deal_name', hide: false, sort: 'asc', sortIndex: 0, width: 222 }, { colId: 'dv01', hide: false, sort: 'desc', sortIndex: 1 }],
      }));
      const saved = api.getColumnState();
      expect(saved.filter((c) => c.sort).map((c) => [c.colId, c.sort, c.sortIndex])).toEqual([['deal_name', 'asc', 0], ['dv01', 'desc', 1]]);
      expect(saved.find((c) => c.colId === 'deal_name')).toMatchObject({ width: 222, pinned: 'left', hide: false });
    } finally {
      api.destroy();
    }
  });

  it('cycles presets and wraps', () => {
    expect(nextPreset(presets, 'Risk')).toBe('All');
    expect(nextPreset(presets, 'Mine')).toBe('Risk');
    expect(nextPreset(presets, 'gone')).toBe('Risk');
    expect(nextPreset([], 'Risk')).toBe('Risk');
  });

  it('opens the remembered preset if it still exists, else Risk', () => {
    expect(initialPreset(presets, 'Mine')).toBe('Mine');
    expect(initialPreset(presets, 'Deleted')).toBe('Risk');
    expect(initialPreset(presets, null)).toBe('Risk');
  });
});
