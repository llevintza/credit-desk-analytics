import type { ColDef } from 'ag-grid-community';
import { formatCell } from '../core/format';
import { NegativeStyle } from '../core/format';
import { FundPerformance, FundRow } from '../data-access/api.types';

const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** "2026-01-31" → "Jan 26" (README §6 P2 headers). */
export function monthHeader(isoMonthEnd: string): string {
  const [year, month] = isoMonthEnd.split('-');
  return `${monthNames[Number(month) - 1]} ${year.slice(2)}`;
}

/** Field name of a month column: dots and dashes aren't safe in AG Grid field paths. */
export const monthField = (isoMonthEnd: string): string => `m${isoMonthEnd.replaceAll('-', '')}`;

/** Row objects for the grid: the label plus one field per month (the pivot already happened on the server). */
export function fundRows(p: FundPerformance): Record<string, unknown>[] {
  return p.rows.map((row: FundRow) => {
    const out: Record<string, unknown> = { label: row.label, format: row.format };
    p.months.forEach((m, i) => (out[monthField(m)] = row.values[i]));
    return out;
  });
}

/**
 * Column defs built from `months` (README §6 P2): a pinned label column, then one right-aligned column per
 * month-end. Each row carries its own format (money0 for balance, pct2 for IRR).
 */
export function fundColumns(months: string[], negatives: () => NegativeStyle): ColDef[] {
  return [
    { colId: 'label', field: 'label', headerName: '', pinned: 'left', width: 110, cellClass: 'row-label' },
    ...months.map((m): ColDef => ({
      colId: m,
      field: monthField(m),
      headerName: monthHeader(m),
      headerTooltip: m,
      type: 'rightAligned',
      width: 112,
      cellClass: 'num',
      valueFormatter: (p) => formatCell(p.data?.format === 'pct2' ? 'Pct' : 'Money', p.value, negatives()),
      cellClassRules: { neg: (p) => typeof p.value === 'number' && p.value < 0 },
    })),
  ];
}
