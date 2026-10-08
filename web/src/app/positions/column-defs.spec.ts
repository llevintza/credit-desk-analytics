import type { ColDef } from 'ag-grid-community';
import { CatalogColumn } from '../data-access/api.types';
import { columnDefs, heat, isScenario } from './column-defs';

const catalog: CatalogColumn[] = [
  { name: 'position_id', group: 'Keys', kind: 'Key', aggregation: 'None', header: 'Position' },
  { name: 'deal_name', group: 'Keys', kind: 'Text', aggregation: 'None', header: 'Deal' },
  { name: 'market_value', group: 'Holding', kind: 'Money', aggregation: 'Sum', header: 'Market value' },
  { name: 'next_pay_date', group: 'Cash flow', kind: 'Date', aggregation: 'None', header: 'Next pay' },
  { name: 'watchlist_flag', group: 'Flags', kind: 'Flag', aggregation: 'None', header: 'Watchlist' },
  { name: 'scn_rp100_sp50', group: 'Scenarios', kind: 'Price', aggregation: 'WeightedByMarketValue', header: '+100/+50' },
];

const leaf = (name: string): ColDef =>
  columnDefs(catalog, () => 'minus').flatMap((g) => g.children as ColDef[]).find((c) => c.colId === name)!;

describe('columnDefs', () => {
  it('groups by catalog group and skips internal ids', () => {
    const groups = columnDefs(catalog, () => 'minus');
    expect(groups.map((g) => g.headerName)).toEqual(['Keys', 'Holding', 'Cash flow', 'Flags', 'Scenarios']);
    expect(groups[0].children.map((c) => (c as ColDef).colId)).toEqual(['deal_name']);
  });

  it('pins identity columns left, right-aligns numbers and starts every column hidden', () => {
    expect(leaf('deal_name').pinned).toBe('left');
    expect(leaf('market_value').pinned).toBeNull();
    expect(leaf('market_value').type).toBe('rightAligned');
    expect(leaf('market_value').cellClass).toBe('num');
    expect(leaf('watchlist_flag').cellClass).toBe('flag');
    expect(leaf('deal_name').type).toBeUndefined();
    expect(Object.values(catalog).every((c) => c.name === 'position_id' || leaf(c.name).hide)).toBe(true);
  });

  it('picks the Community filter for each kind', () => {
    expect(leaf('deal_name').filter).toBe('agTextColumnFilter');
    expect(leaf('market_value').filter).toBe('agNumberColumnFilter');
    expect(leaf('next_pay_date').filter).toBe('agDateColumnFilter');
    expect(leaf('watchlist_flag').filter).toBe(false);
  });

  it('formats with the current negative style and gives full precision in the tooltip', () => {
    let style: 'minus' | 'parens' = 'minus';
    const mv = columnDefs(catalog, () => style).flatMap((g) => g.children as ColDef[]).find((c) => c.colId === 'market_value')!;
    const fmt = mv.valueFormatter as (p: { value: unknown }) => string;
    expect(fmt({ value: -1500.4 })).toBe('-1,500');
    style = 'parens';
    expect(fmt({ value: -1500.4 })).toBe('(1,500)');
    expect((mv.tooltip as (p: { value: unknown }) => string)({ value: 1234.567 })).toBe('1234.567');
    const neg = (mv.cellClassRules as Record<string, (p: { value: unknown }) => boolean>)['neg'];
    expect(neg({ value: -1 })).toBe(true);
    expect(neg({ value: 1 })).toBe(false);
  });

  it('heat-maps scenario prices against the base price', () => {
    expect(isScenario('scn_rp100_sp50')).toBe(true);
    expect(isScenario('price')).toBe(false);
    const style = leaf('scn_rp100_sp50').cellStyle as (p: { value: unknown; data?: { price?: number } }) => unknown;
    expect(style({ value: 95, data: { price: 100 } })).toEqual({ backgroundColor: 'color-mix(in srgb, var(--down) 23%, transparent)' });
    expect(style({ value: 120, data: { price: 100 } })).toEqual({ backgroundColor: 'color-mix(in srgb, var(--up) 45%, transparent)' });
    expect(heat(100, 100)).toBeNull();
    expect(heat(null, 100)).toBeNull();
    expect(heat(95, undefined)).toBeNull();
    expect(leaf('market_value').cellStyle).toBeUndefined();
  });
});
