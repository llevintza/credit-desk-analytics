import type { ColumnState } from 'ag-grid-community';
import { Preset, PresetState } from '../data-access/api.types';

export const page = 'positions';
export const defaultPreset = 'Risk';

/** A built-in preset lists columns; as grid state that's "these visible, in this order, everything else hidden". */
export function columnStateOf(state: PresetState): ColumnState[] {
  if (state.columnState) return state.columnState as ColumnState[];
  return (state.columns ?? []).map((colId) => ({ colId, hide: false }));
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
