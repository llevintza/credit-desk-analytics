import { Mark, MarkBook, SseReader } from './marks';

const mark = (bondId: number, seq: number, price = 100, spreadBp: number | null = 200): Mark =>
  ({ bondId, seq, eventTs: '2026-10-16T09:30:00Z', price, spreadBp });

describe('SseReader', () => {
  it('returns each event once, as soon as its blank line arrives, from a growing response text', () => {
    const reader = new SseReader();
    let text = 'retry: 5000\nevent: hello\ndata: {"bonds":2}\n\nid: 7\nevent: mark\nda';
    expect(reader.read(text)).toEqual([{ event: 'hello', data: '{"bonds":2}', id: null }]);
    text += 'ta: {"bondId":1}\n';
    expect(reader.read(text)).toEqual([]); // not finished until the blank line
    text += '\n: a comment\n\nevent: bye\ndata: {"reason":"lifetime"}\n\n';
    expect(reader.read(text)).toEqual([
      { event: 'mark', data: '{"bondId":1}', id: '7' },
      { event: 'bye', data: '{"reason":"lifetime"}', id: null },
    ]);
    expect(reader.read(text)).toEqual([]); // the same text again yields nothing new
  });

  it('accepts CRLF and CR line ends, even when a CRLF is split across chunks', () => {
    const reader = new SseReader();
    let text = 'data: a\r';
    expect(reader.read(text)).toEqual([]);
    text += '\n\r\ndata:b\rdata:c\r\r';
    // The last CR may still be half of a CRLF, so the second event waits for the next chunk.
    expect(reader.read(text)).toEqual([{ event: 'message', data: 'a', id: null }]);
    text += 'data: d\n\n';
    expect(reader.read(text)).toEqual([
      { event: 'message', data: 'b\nc', id: null },
      { event: 'message', data: 'd', id: null },
    ]);
  });

  it('ignores events without data and fields it does not know, and starts over for a new response', () => {
    const reader = new SseReader();
    expect(reader.read('event: ping\n\nfoo: bar\ndata\n\n')).toEqual([{ event: 'message', data: '', id: null }]);
    expect(reader.read('data: x\n\n')).toEqual([{ event: 'message', data: 'x', id: null }]); // shorter: a new response
    reader.reset();
    expect(reader.read('data: y\n\n')).toEqual([{ event: 'message', data: 'y', id: null }]);
  });
});

describe('MarkBook', () => {
  it('ignores a mark whose seq is at or below the one applied for its bond (stale after a reconnect)', () => {
    const book = new MarkBook();
    expect(book.lastSeq).toBeNull();
    expect(book.apply(mark(1, 10, 101))).toBe(true);
    expect(book.apply(mark(1, 10, 99))).toBe(false); // a replay of the same seq
    expect(book.apply(mark(1, 9, 98))).toBe(false); // an older one
    expect(book.get(1)?.price).toBe(101);
    expect(book.apply(mark(2, 4))).toBe(true); // another bond keeps its own seq
    expect(book.apply(mark(1, 11, 102))).toBe(true);
    expect(book.lastSeq).toBe(11);
    expect(book.size).toBe(2);
    book.clear();
    expect(book.size).toBe(0);
    expect(book.lastSeq).toBeNull();
    expect(book.apply(mark(1, 1))).toBe(true); // a cleared book starts over
  });

  it('overlays a marked bond onto the marked columns a row has, as a new row', () => {
    const book = new MarkBook();
    book.apply(mark(7, 3, 101.25, 180.5));
    const row = { position_id: 1, bond_id: 7, price: 99, spread_bp: 210, deal_name: 'CLO 1' };
    const next = book.overlay(row);
    expect(next).toEqual({ position_id: 1, bond_id: 7, price: 101.25, spread_bp: 180.5, deal_name: 'CLO 1' });
    expect(next).not.toBe(row);
    expect(row.price).toBe(99); // the row it was given is untouched
    expect(book.overlay({ bond_id: 7, deal_name: 'x' })).toEqual({ bond_id: 7, deal_name: 'x' }); // no marked columns shown
    const unmarked = { bond_id: 8, price: 90 };
    expect(book.overlay(unmarked)).toBe(unmarked);
    const noBond = { price: 90 };
    expect(book.overlay(noBond)).toBe(noBond);
  });
});
