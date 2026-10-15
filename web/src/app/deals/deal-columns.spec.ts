import type { ColDef, ValueFormatterParams } from 'ag-grid-community';
import { RemitMonth, Tranche } from '../data-access/api.types';
import { capitalStack, dealListColumns, positionColumns, trancheColumns, trendRows } from './deal-columns';

const tranche = (bondId: number, cls: string, currentBalance: number): Tranche => ({
  bondId, class: cls, cusip: `C${bondId}`, seniorityRank: bondId, originalBalance: currentBalance, currentBalance, factor: 1,
  couponType: 'FLOAT', couponOrMargin: 0.015, couponIndex: 'SOFR', ratingSp: 'AAA', ratingMoodys: 'Aaa', ratingFitch: 'AAA',
  legalFinal: '2040-01-15', expectedMaturity: '2032-01-15',
});

const month = (period: string, oc: number | null): RemitMonth => ({
  period, loanCount: 1200, dq30: 0.021, dq60: 0.011, dq90Plus: 0.006, cpr: 0.08, cdr: 0.01, severity: 0.35, ocCushion: oc, icCushion: null,
});

const fmt = (col: ColDef, value: unknown): string =>
  (col.valueFormatter as (p: ValueFormatterParams) => string)({ value } as ValueFormatterParams);

describe('Deal Explorer columns (README §6 P4)', () => {
  it('lists deal terms, the representative bond and the holdings, with the deal name pinned', () => {
    const cols = dealListColumns(() => 'minus');
    expect(cols.map((c) => c.field)).toEqual([
      'name', 'sector', 'subSector', 'vintage', 'status', 'originalBalance', 'bondCount', 'currentBalance', 'repClass', 'repCusip',
      'repCurrentBalance', 'repCouponType', 'repCoupon', 'repRating', 'positions', 'heldFace', 'heldMarketValue',
    ]);
    expect(cols[0].pinned).toBe('left');
    const byField = Object.fromEntries(cols.map((c) => [c.field, c]));
    expect(fmt(byField['heldMarketValue'], 1234567.891)).toBe('1,234,568');
    expect(fmt(byField['repCoupon'], 0.0155)).toBe('1.55%');
    expect(fmt(byField['vintage'], 2021)).toBe('2021');
    // A zero-bond deal: blank, not zero.
    expect(fmt(byField['repCurrentBalance'], null)).toBe('');
    expect(fmt(byField['currentBalance'], null)).toBe('');
  });

  it('formats negatives in the chosen style and flags them for the red class', () => {
    const [parens] = positionColumns(() => 'parens').filter((c) => c.field === 'marketValue');
    expect(fmt(parens, -2500)).toBe('(2,500)');
    const rule = (parens.cellClassRules as Record<string, (p: { value: unknown }) => boolean>)['neg'];
    expect(rule({ value: -1 })).toBe(true);
    expect(rule({ value: 1 })).toBe(false);
    expect((parens.tooltipValueGetter as (p: { value: unknown }) => string)({ value: -2500.25 })).toBe('-2500.25');
  });

  it('shows the capital structure in seniority order with ratings and coupon', () => {
    const cols = trancheColumns(() => 'minus');
    expect(cols.map((c) => c.field)).toContain('ratingMoodys');
    expect(fmt(cols.find((c) => c.field === 'factor')!, 0.8123)).toBe('0.81');
  });

  it('turns tranches into stack bars that share the deal balance, never NaN', () => {
    const bars = capitalStack([tranche(1, 'A', 300), tranche(2, 'B', 100), tranche(3, 'E', 0)]);
    expect(bars.map((b) => [b.label, b.share])).toEqual([['A', 0.75], ['B', 0.25], ['E', 0]]);
    expect(capitalStack([tranche(1, 'A', 0)])[0].share).toBe(0);
    expect(capitalStack([])).toEqual([]);
  });

  it('lays the surveillance trend out as metric rows over months and drops metrics the deal never reports', () => {
    const rows = trendRows([month('2026-08-31', 0.04), month('2026-09-30', null)]);
    const ids = rows.map((r) => r.id);
    expect(ids).toContain('ocCushion');
    expect(ids).not.toContain('icCushion');
    expect(rows.find((r) => r.id === 'ocCushion')!.values).toEqual([0.04, null]);
    expect(rows.find((r) => r.id === 'loanCount')!.kind).toBe('Count');
    expect(trendRows([])).toEqual([]);
  });
});
