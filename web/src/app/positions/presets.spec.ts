import { Preset } from '../data-access/api.types';
import { columnStateOf, initialPreset, nextPreset } from './presets';

const presets: Preset[] = [
  { name: 'Risk', builtIn: true, state: { columns: ['deal_name', 'dv01'] }, updatedAt: null },
  { name: 'All', builtIn: true, state: { columns: [] }, updatedAt: null },
  { name: 'Mine', builtIn: false, state: { columnState: [{ colId: 'cs01', hide: false, width: 90 }] }, updatedAt: 'x' },
];

describe('presets', () => {
  it('turns a built-in column list into visible column state, and passes saved state through', () => {
    expect(columnStateOf(presets[0].state)).toEqual([{ colId: 'deal_name', hide: false }, { colId: 'dv01', hide: false }]);
    expect(columnStateOf(presets[2].state)).toEqual([{ colId: 'cs01', hide: false, width: 90 }]);
    expect(columnStateOf({})).toEqual([]);
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
