import { Cell, ColumnKind } from '../data-access/api.types';

export type NegativeStyle = 'minus' | 'parens';

/** Display decimals per catalog kind (README §9.1). Percentages arrive as ratios and show ×100. */
const decimals: Record<ColumnKind, number> = {
  Key: 0, Count: 0, Money: 0, Price: 3, Bp: 0, Pct: 2, Ratio: 2, Text: 0, Date: 0, Flag: 0,
};

const numeric = new Set<ColumnKind>(['Key', 'Count', 'Money', 'Price', 'Bp', 'Pct', 'Ratio']);

export const isNumeric = (kind: ColumnKind): boolean => numeric.has(kind);

/**
 * One Intl.NumberFormat per (kind) for the whole app (README §9.4: never one per cell). Formatting a value is a
 * map lookup plus `format`, so a 200-column block formats without allocating formatters.
 */
const formatters = new Map<ColumnKind, Intl.NumberFormat>();

function formatter(kind: ColumnKind): Intl.NumberFormat {
  let f = formatters.get(kind);
  if (!f) {
    const d = decimals[kind];
    // Ids and years read better without grouping separators.
    f = new Intl.NumberFormat('en-US', { minimumFractionDigits: d, maximumFractionDigits: d, useGrouping: kind !== 'Key' && kind !== 'Count' });
    formatters.set(kind, f);
  }
  return f;
}

/** The text a grid cell shows for a catalog value. Null, NaN and empty values show as nothing. */
export function formatCell(kind: ColumnKind, value: Cell, negatives: NegativeStyle = 'minus'): string {
  if (value === null || value === undefined || value === '') return '';
  if (kind === 'Flag') return value === true ? '✓' : '';
  if (kind === 'Text' || kind === 'Date') return String(value);
  const n = typeof value === 'number' ? value : Number(value);
  if (!Number.isFinite(n)) return '';
  const scaled = kind === 'Pct' ? n * 100 : n;
  const text = formatter(kind).format(Math.abs(scaled)) + (kind === 'Pct' ? '%' : '');
  // -0.0004 rounds to "0.000": no sign for a value that displays as zero.
  const shownZero = Number(formatter(kind).format(Math.abs(scaled)).replace(/,/g, '')) === 0;
  if (scaled >= 0 || shownZero) return text;
  return negatives === 'parens' ? `(${text})` : `-${text}`;
}

/** Full precision for the hover tooltip (README §9.1). */
export function fullPrecision(kind: ColumnKind, value: Cell): string {
  if (value === null || value === undefined) return '';
  // ×100 in binary floating point shows noise (0.07 → 7.000000000000001): 12 significant digits drops it.
  if (kind === 'Pct' && typeof value === 'number') return `${Number((value * 100).toPrecision(12))}%`;
  return String(value);
}

/** Up/down tone for a numeric cell: drives the colour class (and the colorblind palette swaps the colours). */
export function tone(value: Cell): 'up' | 'down' | null {
  if (typeof value !== 'number' || !Number.isFinite(value) || value === 0) return null;
  return value > 0 ? 'up' : 'down';
}

/** Internal: how many formatters exist (tests prove they're cached). */
export const formatterCount = (): number => formatters.size;
