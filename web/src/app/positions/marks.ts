import { Mark } from '../data-access/api.types';
import { Row } from '../data-access/to-rows';

export type { Mark, MarkBye, MarkHello, MarkTotals } from '../data-access/api.types';

/** The grid columns a mark replaces, and the mark field each one takes. */
export const markedColumns = { price: 'price', spread_bp: 'spreadBp' } as const;
export type MarkedColumn = keyof typeof markedColumns;
export const markedColumnIds = Object.keys(markedColumns) as MarkedColumn[];

export interface SseEvent { event: string; data: string; id: string | null; }

/**
 * An incremental `text/event-stream` parser (the WHATWG rules the API's stream needs): give it the response text
 * as it grows and it returns the events completed since the last call. Fields other than event, data and id are
 * ignored; so are comments and events without data.
 */
export class SseReader {
  private seen = 0;
  private pending = '';

  read(text: string): SseEvent[] {
    // A new response starts again at zero.
    if (text.length < this.seen) this.reset();
    this.pending += text.slice(this.seen);
    this.seen = text.length;
    // CRLF and CR end lines too; a CR at the very end may be half of a CRLF, so it waits for the next chunk.
    const hold = this.pending.endsWith('\r') ? '\r' : '';
    const normalized = (hold ? this.pending.slice(0, -1) : this.pending).replace(/\r\n?/g, '\n');
    const blocks = normalized.split('\n\n');
    this.pending = blocks.pop()! + hold;
    return blocks.map(parseBlock).filter((e): e is SseEvent => e !== null);
  }

  reset(): void {
    this.seen = 0;
    this.pending = '';
  }
}

function parseBlock(block: string): SseEvent | null {
  let event = 'message';
  let id: string | null = null;
  const data: string[] = [];
  for (const line of block.split('\n')) {
    if (line === '' || line.startsWith(':')) continue;
    const colon = line.indexOf(':');
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1);
    if (field === 'event') event = value;
    else if (field === 'data') data.push(value);
    else if (field === 'id') id = value;
  }
  return data.length ? { event, data: data.join('\n'), id } : null;
}

/**
 * The marks applied so far, per bond. A mark whose `seq` is at or below the one already applied for its bond is
 * ignored (README §6 P6), so a replay after a reconnect, a restart or an out-of-order delivery never puts an older
 * price back on screen.
 */
export class MarkBook {
  private readonly marks = new Map<number, Mark>();
  private max: number | null = null;

  /** True when the mark was applied; false when it is stale. */
  apply(mark: Mark): boolean {
    const last = this.marks.get(mark.bondId);
    if (last && mark.seq <= last.seq) return false;
    this.marks.set(mark.bondId, mark);
    if (this.max === null || mark.seq > this.max) this.max = mark.seq;
    return true;
  }

  get(bondId: number): Mark | undefined {
    return this.marks.get(bondId);
  }

  /** The highest seq applied: what a reconnect asks to resume after. */
  get lastSeq(): number | null {
    return this.max;
  }

  get size(): number {
    return this.marks.size;
  }

  /** The row with its bond's mark on the marked columns it has; the same row when its bond has no mark. */
  overlay(row: Row): Row {
    const mark = typeof row['bond_id'] === 'number' ? this.marks.get(row['bond_id']) : undefined;
    if (!mark) return row;
    const next: Row = { ...row };
    for (const col of markedColumnIds) if (col in row) next[col] = mark[markedColumns[col]];
    return next;
  }

  clear(): void {
    this.marks.clear();
    this.max = null;
  }
}
