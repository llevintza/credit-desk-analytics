import { Cell, GridBlock } from './api.types';

export type Row = Record<string, Cell>;

/**
 * Columnar block → row objects for AG Grid (README §9.4). Only the block being handed to the grid is converted;
 * the transpose allocates one object per row and reads each cell once.
 */
export function toRows(block: Pick<GridBlock, 'columns' | 'data'>): Row[] {
  const { columns, data } = block;
  const count = data.length === 0 ? 0 : data[0].length;
  const rows = new Array<Row>(count);
  for (let r = 0; r < count; r++) {
    const row: Row = {};
    for (let c = 0; c < columns.length; c++) row[columns[c]] = data[c][r];
    rows[r] = row;
  }
  return rows;
}
