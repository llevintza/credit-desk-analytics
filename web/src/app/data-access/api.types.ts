/** DTOs of the Desk API (README §8). The only place the wire shapes are written down on the client. */

export interface Health { status: string; version: string; maintenance?: boolean; }

export interface Me { email: string; roles: string[]; expiresAt: string; }

export interface AsOf { latest: string; dates: string[]; }

export type ColumnKind = 'Key' | 'Text' | 'Date' | 'Money' | 'Price' | 'Bp' | 'Pct' | 'Ratio' | 'Count' | 'Flag';
export type Aggregation = 'None' | 'Sum' | 'WeightedByMarketValue';

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

/** What the status bar shows about the last data request (README §9.2). */
export interface RequestInfo {
  ms: number; cache: 'HIT' | 'MISS' | null; bytes: number | null; serverMs: number | null;
  /** True when overlapping blocks make `bytes` approximate (shown as "≈", see #222); absent means exact. */
  bytesApprox?: boolean;
}
