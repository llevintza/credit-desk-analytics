import type { ColDef } from 'ag-grid-community';
import { NegativeStyle, formatCell, fullPrecision } from '../core/format';
import { ColumnKind, DealPosition, DealRow, RemitMonth, Tranche } from '../data-access/api.types';

/** A right-aligned numeric column in a catalog kind (README §9.1 formats, full precision on hover, red negatives). */
function num<T>(field: keyof T & string, headerName: string, kind: ColumnKind, negatives: () => NegativeStyle, width = 120): ColDef {
  return {
    colId: field,
    field,
    headerName,
    type: 'rightAligned',
    width,
    cellClass: 'num',
    filter: 'agNumberColumnFilter',
    valueFormatter: (p) => formatCell(kind, p.value as number | null, negatives()),
    tooltipValueGetter: (p) => fullPrecision(kind, p.value as number | null),
    cellClassRules: { neg: (p) => typeof p.value === 'number' && p.value < 0 },
  };
}

function text<T>(field: keyof T & string, headerName: string, width = 110): ColDef {
  return { colId: field, field, headerName, width, filter: 'agTextColumnFilter' };
}

/**
 * The deal list (README §6 P4): deal terms, its representative (most senior) bond and the scope's holdings. A deal
 * with no bonds yet shows blank bond columns, not zeros.
 */
export function dealListColumns(negatives: () => NegativeStyle): ColDef[] {
  return [
    { ...text<DealRow>('name', 'Deal', 150), pinned: 'left', cellClass: 'row-label' },
    text<DealRow>('sector', 'Sector', 80),
    text<DealRow>('subSector', 'Sub-sector', 110),
    num<DealRow>('vintage', 'Vintage', 'Key', negatives, 84),
    text<DealRow>('status', 'Status', 90),
    num<DealRow>('originalBalance', 'Orig balance', 'Money', negatives, 130),
    num<DealRow>('bondCount', 'Bonds', 'Count', negatives, 76),
    num<DealRow>('currentBalance', 'Cur balance', 'Money', negatives, 130),
    text<DealRow>('repClass', 'Rep class', 84),
    text<DealRow>('repCusip', 'Rep CUSIP', 104),
    num<DealRow>('repCurrentBalance', 'Rep balance', 'Money', negatives, 124),
    text<DealRow>('repCouponType', 'Cpn type', 90),
    num<DealRow>('repCoupon', 'Cpn / margin', 'Pct', negatives, 104),
    text<DealRow>('repRating', 'S&P', 64),
    num<DealRow>('positions', 'Positions', 'Count', negatives, 90),
    num<DealRow>('heldFace', 'Held face', 'Money', negatives, 124),
    num<DealRow>('heldMarketValue', 'Held MV', 'Money', negatives, 124),
  ];
}

/** The capital structure grid, most senior first (the order the API sends). */
export function trancheColumns(negatives: () => NegativeStyle): ColDef[] {
  return [
    { ...text<Tranche>('class', 'Class', 70), cellClass: 'row-label' },
    text<Tranche>('cusip', 'CUSIP', 104),
    num<Tranche>('seniorityRank', 'Rank', 'Count', negatives, 66),
    num<Tranche>('originalBalance', 'Orig balance', 'Money', negatives, 128),
    num<Tranche>('currentBalance', 'Cur balance', 'Money', negatives, 128),
    num<Tranche>('factor', 'Factor', 'Ratio', negatives, 76),
    text<Tranche>('couponType', 'Cpn type', 90),
    num<Tranche>('couponOrMargin', 'Cpn / margin', 'Pct', negatives, 104),
    text<Tranche>('couponIndex', 'Index', 70),
    text<Tranche>('ratingSp', 'S&P', 60),
    text<Tranche>('ratingMoodys', "Moody's", 76),
    text<Tranche>('ratingFitch', 'Fitch', 60),
    text<Tranche>('legalFinal', 'Legal final', 100),
  ];
}

/** The scope's positions in the deal. */
export function positionColumns(negatives: () => NegativeStyle): ColDef[] {
  return [
    { ...text<DealPosition>('portfolio', 'Portfolio', 170), cellClass: 'row-label' },
    num<DealPosition>('positionId', 'Position', 'Key', negatives, 90),
    text<DealPosition>('class', 'Class', 70),
    num<DealPosition>('face', 'Face', 'Money', negatives, 124),
    num<DealPosition>('price', 'Price', 'Price', negatives, 90),
    num<DealPosition>('marketValue', 'Market value', 'Money', negatives, 130),
  ];
}

/** One bar of the capital structure stack: the tranche and its share of the deal's current balance. */
export interface StackBar { bondId: number; label: string; balance: number; share: number; }

/**
 * The capital structure stack (README §6 P4), most senior on top: each tranche's current balance as a share of the
 * deal's. Shares are 0 when the deal has no balance left (never NaN).
 */
export function capitalStack(tranches: Tranche[]): StackBar[] {
  const total = tranches.reduce((sum, t) => sum + Math.max(0, t.currentBalance), 0);
  return tranches.map((t) => ({
    bondId: t.bondId,
    label: t.class,
    balance: t.currentBalance,
    share: total > 0 ? Math.max(0, t.currentBalance) / total : 0,
  }));
}

/** The surveillance trend as rows of metrics × months (oldest first), the way P2 lays out time. */
export interface TrendRow { id: keyof RemitMonth; label: string; kind: ColumnKind; values: (number | null)[]; }

const trendMetrics: { id: keyof RemitMonth; label: string; kind: ColumnKind }[] = [
  { id: 'dq30', label: '30+ DQ', kind: 'Pct' },
  { id: 'dq60', label: '60+ DQ', kind: 'Pct' },
  { id: 'dq90Plus', label: '90+ DQ', kind: 'Pct' },
  { id: 'cpr', label: 'CPR', kind: 'Pct' },
  { id: 'cdr', label: 'CDR', kind: 'Pct' },
  { id: 'severity', label: 'Severity', kind: 'Pct' },
  { id: 'ocCushion', label: 'OC cushion', kind: 'Pct' },
  { id: 'icCushion', label: 'IC cushion', kind: 'Pct' },
  { id: 'loanCount', label: 'Loans', kind: 'Count' },
];

/** Metric rows for the trend table; a metric the deal never reports (OC/IC outside CLOs) is left out. */
export function trendRows(months: RemitMonth[]): TrendRow[] {
  return trendMetrics
    .map((m) => ({ ...m, values: months.map((x) => (x[m.id] as number | null) ?? null) }))
    .filter((r) => r.values.some((v) => v !== null));
}
