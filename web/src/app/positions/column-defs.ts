import type { CellClassParams, ColDef, ColGroupDef, ValueFormatterParams } from 'ag-grid-community';
import { CatalogColumn } from '../data-access/api.types';
import { NegativeStyle, formatCell, fullPrecision, isNumeric } from '../core/format';

/** Identity columns pinned to the left (README §6 P1). */
export const pinnedLeft = ['deal_name', 'class', 'cusip'];

/** Internal ids and the as-of date (shown in the top bar) are never grid columns. */
const internal = new Set(['as_of_date', 'position_id', 'portfolio_id', 'fund_id', 'bond_id', 'deal_id']);

export const isScenario = (name: string): boolean => name.startsWith('scn_');

/**
 * Scenario heat map (README §9.1): the shocked price against the position's base price, on a diverging scale.
 * A ±10 point move is full intensity. Colours are the theme's up/down variables, so the colorblind palette applies.
 */
export function heat(value: unknown, base: unknown): Record<string, string> | null {
  if (typeof value !== 'number' || typeof base !== 'number' || !Number.isFinite(value) || !Number.isFinite(base)) return null;
  const diff = value - base;
  if (diff === 0) return null;
  const pct = Math.round(Math.min(1, Math.abs(diff) / 10) * 45);
  return { backgroundColor: `color-mix(in srgb, var(${diff > 0 ? '--up' : '--down'}) ${pct}%, transparent)` };
}

function filterFor(c: CatalogColumn): string | false {
  if (c.kind === 'Text') return 'agTextColumnFilter';
  if (c.kind === 'Date') return 'agDateColumnFilter';
  if (isNumeric(c.kind)) return 'agNumberColumnFilter';
  return false; // flags: no Community filter fits (the set filter is Enterprise)
}

/**
 * AG Grid column definitions generated from the catalog (README §5.3: the catalog drives the UI), grouped by
 * catalog group. Every column exists but starts hidden; presets decide what's visible, and only visible columns
 * are requested from the API.
 */
export function columnDefs(catalog: CatalogColumn[], negatives: () => NegativeStyle): ColGroupDef[] {
  const groups = new Map<string, ColDef[]>();
  for (const c of catalog) {
    if (internal.has(c.name)) continue;
    const numeric = isNumeric(c.kind);
    const def: ColDef = {
      colId: c.name,
      field: c.name,
      headerName: c.header,
      headerTooltip: `${c.header} (${c.name})`,
      hide: true,
      sortable: true,
      filter: filterFor(c),
      pinned: pinnedLeft.includes(c.name) ? 'left' : null,
      lockPinned: pinnedLeft.includes(c.name),
      type: numeric ? 'rightAligned' : undefined,
      width: numeric ? 104 : c.kind === 'Text' ? 150 : 110,
      valueFormatter: (p: ValueFormatterParams) => formatCell(c.kind, p.value, negatives()),
      tooltip: (p) => fullPrecision(c.kind, p.value),
      cellClass: numeric ? 'num' : c.kind === 'Flag' ? 'flag' : undefined,
      cellClassRules: numeric ? { neg: (p: CellClassParams) => typeof p.value === 'number' && p.value < 0 } : undefined,
      cellStyle: isScenario(c.name) ? (p: CellClassParams) => heat(p.value, p.data?.price) : undefined,
    };
    const list = groups.get(c.group) ?? [];
    list.push(def);
    groups.set(c.group, list);
  }
  return [...groups].map(([group, children]) => ({ headerName: group, groupId: group, children }));
}
