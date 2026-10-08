import { toRows } from './to-rows';

describe('toRows', () => {
  it('transposes data[col][row] into one object per row', () => {
    const rows = toRows({
      columns: ['position_id', 'deal_name', 'dv01'],
      data: [[1, 2], ['CLO 2024-3', null], [512.3, -4]],
    });
    expect(rows).toEqual([
      { position_id: 1, deal_name: 'CLO 2024-3', dv01: 512.3 },
      { position_id: 2, deal_name: null, dv01: -4 },
    ]);
  });

  it('returns no rows for an empty block', () => {
    expect(toRows({ columns: ['position_id'], data: [] })).toEqual([]);
    expect(toRows({ columns: ['position_id'], data: [[]] })).toEqual([]);
  });
});
