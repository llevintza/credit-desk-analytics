import { NegativeStyle } from '../core/format';

/**
 * P3 tile cells by the format the API names per column (README §6 P3). Money is compact (1.2B) because a tile is
 * ~320 px wide; the full value is in the cell's tooltip. One Intl.NumberFormat per format (README §9.4).
 */
interface Spec { options: Intl.NumberFormatOptions; scale?: number; suffix?: string; }

const specs: Record<string, Spec> = {
  money0: { options: { notation: 'compact', maximumFractionDigits: 1 } },
  pct0: { options: { maximumFractionDigits: 0, minimumFractionDigits: 0 }, scale: 100, suffix: '%' },
  pct1: { options: { maximumFractionDigits: 1, minimumFractionDigits: 1 }, scale: 100, suffix: '%' },
  pct2: { options: { maximumFractionDigits: 2, minimumFractionDigits: 2 }, scale: 100, suffix: '%' },
  bp1: { options: { maximumFractionDigits: 1, minimumFractionDigits: 1 } },
  int: { options: { maximumFractionDigits: 0 } },
  num0: { options: { maximumFractionDigits: 0 } },
  num2: { options: { maximumFractionDigits: 2, minimumFractionDigits: 2 } },
};

const specFor = (format: string): Spec => specs[format] ?? specs['num2'];

const formatters = new Map<string, Intl.NumberFormat>();

function formatter(format: string): Intl.NumberFormat {
  let f = formatters.get(format);
  if (!f) {
    f = new Intl.NumberFormat('en-US', specFor(format).options);
    formatters.set(format, f);
  }
  return f;
}

export const isNumericFormat = (format: string | undefined): boolean => format !== undefined && format !== 'text';

/** The text a tile cell shows. Null and non-finite values show as nothing; a value that rounds to zero has no sign. */
export function formatInsight(format: string | undefined, value: string | number | null, negatives: NegativeStyle = 'minus'): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'string' || !isNumericFormat(format)) return String(value);
  if (!Number.isFinite(value)) return '';
  const spec = specFor(format!);
  const scaled = value * (spec.scale ?? 1);
  const abs = formatter(format!).format(Math.abs(scaled)) + (spec.suffix ?? '');
  // -0.0004 shows as "0.0": no sign on a value that displays as zero.
  if (scaled >= 0 || Number(abs.replace(/[^\d.]/g, '')) === 0) return abs;
  return negatives === 'parens' ? `(${abs})` : `-${abs}`;
}

/** Full precision for the tooltip. */
export function insightTooltip(format: string | undefined, value: string | number | null): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'number' && format?.startsWith('pct')) return `${Number((value * 100).toPrecision(12))}%`;
  return String(value);
}

/** Negative numbers get the down colour (the colorblind palette swaps it). */
export const isNegative = (value: string | number | null): boolean => typeof value === 'number' && value < 0;
