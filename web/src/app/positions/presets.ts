import type { ApplyColumnStateParams, ColumnState } from 'ag-grid-community';
import { Preset, PresetState } from '../data-access/api.types';
import { pinnedLeft } from './column-defs';

export const page = 'positions';
export const defaultPreset = 'Risk';

/**
 * A built-in preset lists columns; as grid state that's "these visible, in this order, unsorted, everything else
 * hidden". A saved preset carries its own state, sort included.
 */
export function columnStateOf(state: PresetState): ColumnState[] {
  if (state.columnState) return state.columnState as ColumnState[];
  return (state.columns ?? []).map((colId) => ({ colId, hide: false, sort: null }));
}

/**
 * What the grid applies for a preset. Identity columns stay visible and pinned whatever the preset says, and a
 * column the preset doesn't mention is hidden and unsorted, so the previous view's sort never leaks into the next
 * request.
 */
export function presetColumnState(state: PresetState): ApplyColumnStateParams {
  const pinned = pinnedLeft.map((colId) => ({ colId, hide: false, pinned: 'left' as const }));
  const rest = columnStateOf(state).filter((s) => !pinnedLeft.includes(s.colId));
  return { state: [...pinned, ...rest], applyOrder: true, defaultState: { hide: true, sort: null } };
}

/** The next preset in the list, wrapping around (Ctrl+Shift+P). */
export function nextPreset(presets: Preset[], current: string): string {
  if (presets.length === 0) return current;
  const i = presets.findIndex((p) => p.name === current);
  return presets[(i + 1) % presets.length].name;
}

/** The preset to open with: the last one used if it still exists, else Risk. */
export function initialPreset(presets: Preset[], remembered: string | null): string {
  return presets.some((p) => p.name === remembered) ? remembered! : defaultPreset;
}
