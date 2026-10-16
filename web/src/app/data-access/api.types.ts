/** DTOs of the Desk API (README §8). The only place the wire shapes are written down on the client. */

export interface Health { status: string; version: string; maintenance?: boolean; }

export interface Me { email: string; roles: string[]; expiresAt: string; }

export interface AsOf { latest: string; dates: string[]; }

export type ColumnKind = 'Key' | 'Text' | 'Date' | 'Money' | 'Price' | 'Bp' | 'Pct' | 'Ratio' | 'Count' | 'Flag';
export type Aggregation = 'None' | 'Sum' | 'WeightedByMarketValue' | 'WeightedByCurrentFace';

export interface CatalogColumn { name: string; group: string; kind: ColumnKind; aggregation: Aggregation; header: string; }

export interface Portfolio { portfolioId: number; name: string; fundId: number; fundName: string; }

export interface Preset { name: string; builtIn: boolean; state: PresetState; updatedAt: string | null; }

/** Built-ins carry only `columns`; saved presets carry AG Grid column state and the filter model too. */
export interface PresetState {
  columns?: string[];
  columnState?: unknown[];
  filterModel?: Record<string, unknown>;
}

export interface SortSpec { colId: string; sort: 'asc' | 'desc'; }

export interface GridRequest {
  asOf?: string;
  portfolioIds?: number[];
  startRow: number;
  endRow: number;
  columns: string[];
  sortModel?: SortSpec[];
  filterModel?: Record<string, unknown>;
  quickFilter?: string;
}

export type Cell = string | number | boolean | null;

/** The columnar block: `data[col][row]` (ADR-0007). */
export interface GridBlock {
  columns: string[];
  data: Cell[][];
  rowCount: number;
  summary: Record<string, number | null>;
  asOf: string;
  generatedAt: string;
}

export type FundRange = 'QTD' | 'YTD' | '1Y' | 'ITD' | 'CUSTOM';

export interface FundRow { label: string; format: 'money0' | 'pct2'; values: (number | null)[]; }

/** P2: one value per month in every row (README §6 P2). */
export interface FundPerformance {
  fundId: number;
  fundName: string;
  range: string;
  from: string | null;
  to: string | null;
  months: string[];
  rows: FundRow[];
}

/** What the status bar shows about the last data request (README §9.2). */
export interface RequestInfo {
  ms: number; cache: 'HIT' | 'MISS' | null; bytes: number | null; serverMs: number | null;
  /** True when overlapping blocks make `bytes` approximate (the status bar shows "≈", #221); absent means exact. */
  bytesApprox?: boolean;
}

/** P3 data sources: one endpoint each (README §6 P3). */
export type InsightSource = 'core' | 'market' | 'surveillance' | 'pricing' | 'reference';

/** A display format the API sends per column: text, money0, pct0/1/2, bp1, int, num0/2. */
export type InsightFormat = string;

/** One small grid: headers, rows whose first cell is the row label, and a format per header. */
export interface InsightGrid {
  id: string;
  title: string;
  columns: string[];
  rows: (string | number | null)[][];
  format: Record<string, InsightFormat>;
}

export interface InsightsResult { source: InsightSource; asOf: string; grids: InsightGrid[]; }

/**
 * P4: one deal of the list (README §6 P4). Bond columns (`rep*`, `currentBalance`) are null for a deal with no bonds
 * yet; held columns are null when none of the caller's portfolios holds it. Money arrives as JSON numbers.
 */
export interface DealRow {
  dealId: number; name: string; sector: string; subSector: string; vintage: number; status: string; originalBalance: number;
  bondCount: number; currentBalance: number | null;
  repBondId: number | null; repClass: string | null; repCusip: string | null; repCurrentBalance: number | null;
  repCouponType: string | null; repCoupon: number | null; repRating: string | null;
  positions: number | null; heldFace: number | null; heldMarketValue: number | null;
}

export interface DealList { asOf: string; deals: DealRow[]; }

export interface DealInfo {
  dealId: number; name: string; sector: string; subSector: string; vintage: number; closingDate: string; collateralType: string;
  originalBalance: number; currency: string; status: string;
}

/** One tranche, most senior first. */
export interface Tranche {
  bondId: number; class: string; cusip: string; seniorityRank: number; originalBalance: number; currentBalance: number; factor: number;
  couponType: string; couponOrMargin: number | null; couponIndex: string | null;
  ratingSp: string | null; ratingMoodys: string | null; ratingFitch: string | null; legalFinal: string; expectedMaturity: string;
}

/** One remittance month; rates are ratios. */
export interface RemitMonth {
  period: string; loanCount: number; dq30: number; dq60: number; dq90Plus: number; cpr: number; cdr: number; severity: number;
  ocCushion: number | null; icCushion: number | null;
}

export interface DealPosition {
  positionId: number; portfolioId: number; portfolio: string; bondId: number; class: string | null;
  face: number | null; marketValue: number | null; price: number | null;
}

export interface DealDetail { asOf: string; deal: DealInfo; tranches: Tranche[]; surveillance: RemitMonth[]; positions: DealPosition[]; }
