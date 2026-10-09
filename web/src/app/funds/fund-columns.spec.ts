import type { ColDef } from 'ag-grid-community';
import { FundPerformance } from '../data-access/api.types';
import { fundColumns, fundRows, monthField, monthHeader } from './fund-columns';

const perf: FundPerformance = {
  fundId: 4, fundName: 'Residential Credit Fund', range: 'QTD', from: '2026-07-31', to: '2026-09-30',
  months: ['2026-07-31', '2026-08-31', '2026-09-30'],
  rows: [
    { label: 'Balance', format: 'money0', values: [100000000.4, 104000000, -5] },
    { label: 'IRR', format: 'pct2', values: [0.05, null, -0.012] },
  ],
};

describe('fund columns (README §6 P2)', () => {
  it('reads month headers as "Jan 26"', () => {
    expect(monthHeader('2026-01-31')).toBe('Jan 26');
    expect(monthHeader('2025-12-31')).toBe('Dec 25');
    expect(monthField('2026-01-31')).toBe('m20260131');
  });

  it('builds a pinned label column and one column per month', () => {
    const cols = fundColumns(perf.months, () => 'minus');
    expect(cols.map((c) => c.headerName)).toEqual(['', 'Jul 26', 'Aug 26', 'Sep 26']);
    expect(cols[0].pinned).toBe('left');
    expect(cols.slice(1).every((c) => c.type === 'rightAligned')).toBe(true);
    expect(fundColumns([], () => 'minus').length).toBe(1);
  });

  it('formats each row by its own format and flags negatives', () => {
    let style: 'minus' | 'parens' = 'minus';
    const col = fundColumns(perf.months, () => style)[1] as ColDef;
    const fmt = col.valueFormatter as (p: { value: unknown; data?: { format: string } }) => string;
    expect(fmt({ value: 100000000.4, data: { format: 'money0' } })).toBe('100,000,000');
    expect(fmt({ value: 0.05, data: { format: 'pct2' } })).toBe('5.00%');
    style = 'parens';
    expect(fmt({ value: -0.012, data: { format: 'pct2' } })).toBe('(1.20%)');
    expect(fmt({ value: null })).toBe('');
    const tip = col.tooltip as (p: { value: unknown; data?: { format: string } }) => string;
    expect(tip({ value: 0.062345, data: { format: 'pct2' } })).toBe('6.2345%');
    expect(tip({ value: 100000000.4, data: { format: 'money0' } })).toBe('100000000.4');
    expect(tip({ value: 1 })).toBe('1');
    const neg = (col.cellClassRules as Record<string, (p: { value: unknown }) => boolean>)['neg'];
    expect([neg({ value: -1 }), neg({ value: 1 }), neg({ value: null })]).toEqual([true, false, false]);
  });

  it('turns rows into one object per measure with a field per month', () => {
    expect(fundRows(perf)).toEqual([
      { label: 'Balance', format: 'money0', m20260731: 100000000.4, m20260831: 104000000, m20260930: -5 },
      { label: 'IRR', format: 'pct2', m20260731: 0.05, m20260831: null, m20260930: -0.012 },
    ]);
  });
});
