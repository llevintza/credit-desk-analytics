import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import type { IGetRowsParams } from 'ag-grid-community';
import { GridBlock } from '../data-access/api.types';
import { PositionsQuery, quickFilterDebounceMs } from './positions-query';

const block = (rowCount = 2): GridBlock => ({
  columns: ['position_id', 'dv01'], data: [[1, 2], [10, 20]], rowCount, summary: { dv01: 30 }, asOf: '2026-10-06', generatedAt: 'x',
});

function params(startRow = 0, endRow = 200, sortModel: { colId: string; sort: 'asc' | 'desc' }[] = [], filterModel = {}) {
  const success = vi.fn();
  const fail = vi.fn();
  return {
    p: { startRow, endRow, sortModel, filterModel, successCallback: success, failCallback: fail, context: undefined } as unknown as IGetRowsParams,
    success,
    fail,
  };
}

describe('PositionsQuery', () => {
  let query: PositionsQuery;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), PositionsQuery] });
    query = TestBed.inject(PositionsQuery);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('requests a block with only the displayed columns and hands the grid rows', () => {
    query.update({ columns: ['dv01'] });
    const { p, success } = params();
    query.datasource.getRows(p);
    const req = http.expectOne('/api/positions/query');
    expect(req.request.body).toEqual({ startRow: 0, endRow: 200, columns: ['dv01'] });
    req.flush(block(), { headers: { 'X-Cache': 'MISS', 'Server-Timing': 'db;dur=3, ser;dur=1, total;dur=4.5' } });
    expect(success).toHaveBeenCalledWith([{ position_id: 1, dv01: 10 }, { position_id: 2, dv01: 20 }], 2);
    expect(query.lastInfo()).toMatchObject({ cache: 'MISS', serverMs: 4.5 });
    expect(query.lastBlock()?.summary).toEqual({ dv01: 30 });
    expect(query.completed()).toBe(1);
  });

  it('sends as-of, portfolios, sort, filter and quick filter when set', () => {
    query.update({ asOf: '2026-10-05', portfolioIds: [3], columns: ['dv01'], quickFilter: 'clo' });
    const { p } = params(200, 400, [{ colId: 'dv01', sort: 'desc' }], { sector: { filterType: 'text', type: 'equals', filter: 'CLO' } });
    query.datasource.getRows(p);
    expect(http.expectOne('/api/positions/query').request.body).toEqual({
      asOf: '2026-10-05', portfolioIds: [3], startRow: 200, endRow: 400, columns: ['dv01'],
      sortModel: [{ colId: 'dv01', sort: 'desc' }], filterModel: { sector: { filterType: 'text', type: 'equals', filter: 'CLO' } }, quickFilter: 'clo',
    });
  });

  it('rapid typing in the quick filter results in exactly one completed request (switchMap + debounce)', () => {
    vi.useFakeTimers();
    try {
      const purges = vi.fn();
      query.onViewChanged = () => {
        purges();
        query.datasource.getRows(params().p); // the grid asks again after a purge
      };
      query.update({ columns: ['dv01'] });
      const first = http.expectOne('/api/positions/query'); // in flight for the old view

      for (const text of ['c', 'cl', 'clo', 'clo ', 'clo 2', 'clo 20', 'clo 2024']) {
        query.type(text);
        vi.advanceTimersByTime(quickFilterDebounceMs - 50);
      }
      vi.advanceTimersByTime(quickFilterDebounceMs);

      expect(first.cancelled).toBe(true); // the old view's block was aborted
      const reqs = http.match('/api/positions/query');
      expect(reqs.length).toBe(1);
      expect(reqs[0].request.body.quickFilter).toBe('clo 2024');
      reqs[0].flush(block());
      expect(query.completed()).toBe(1);
      expect(purges).toHaveBeenCalledTimes(2); // columns, then the one debounced quick filter

      query.type('clo 2024 '); // same text after trimming: no new view
      vi.advanceTimersByTime(quickFilterDebounceMs);
      expect(http.match('/api/positions/query').length).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it('only a change to the set of columns is a new view', () => {
    const purges = vi.fn();
    query.onViewChanged = purges;
    query.setColumns(['a', 'b']);
    query.setColumns(['b', 'a']); // reorder: same data
    expect(purges).toHaveBeenCalledTimes(1);
    query.setColumns(['a', 'b', 'c']);
    expect(purges).toHaveBeenCalledTimes(2);
  });

  it('a sort change from the grid aborts the old view and blocks of one view load in parallel', () => {
    query.update({ columns: ['dv01'] });
    query.datasource.getRows(params(0, 200).p);
    query.datasource.getRows(params(200, 400).p);
    const old = http.match('/api/positions/query');
    expect(old.length).toBe(2);

    query.datasource.getRows(params(0, 200, [{ colId: 'dv01', sort: 'asc' }]).p);
    expect(old.every((r) => r.cancelled)).toBe(true);
    http.expectOne('/api/positions/query').flush(block());
  });

  it('a failed block tells the grid and shows an error, and the next success clears it', () => {
    query.update({ columns: ['dv01'] });
    const failed = params();
    query.datasource.getRows(failed.p);
    http.expectOne('/api/positions/query').flush('boom', { status: 500, statusText: 'Server Error' });
    expect(failed.fail).toHaveBeenCalled();
    expect(query.error()).toContain('Could not load');
    expect(query.loading()).toBe(false);

    query.datasource.getRows(params().p);
    http.expectOne('/api/positions/query').flush(block());
    expect(query.error()).toBeNull();
  });
});

describe('PositionsQuery rate limits', () => {
  it('retries a 429 after Retry-After, then succeeds', () => {
    vi.useFakeTimers();
    try {
      TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), PositionsQuery] });
      const query = TestBed.inject(PositionsQuery);
      const http = TestBed.inject(HttpTestingController);
      query.update({ columns: ['dv01'] });
      const { p, success, fail } = params();
      query.datasource.getRows(p);
      http.expectOne('/api/positions/query').flush('slow down', { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '2' } });
      vi.advanceTimersByTime(1999);
      http.expectNone('/api/positions/query');
      vi.advanceTimersByTime(1);
      http.expectOne('/api/positions/query').flush(block());
      expect(success).toHaveBeenCalled();
      expect(fail).not.toHaveBeenCalled();
      http.verify();
    } finally {
      vi.useRealTimers();
    }
  });

  it('works out the wait from Retry-After, bounded, and ignores other errors', async () => {
    const { HttpErrorResponse, HttpHeaders } = await import('@angular/common/http');
    const { retryAfterMs } = await import('./positions-query');
    const err = (status: number, after?: string) =>
      new HttpErrorResponse({ status, headers: after === undefined ? new HttpHeaders() : new HttpHeaders({ 'Retry-After': after }) });
    expect(retryAfterMs(err(429, '5'))).toBe(5000);
    expect(retryAfterMs(err(429))).toBe(1000);
    expect(retryAfterMs(err(429, 'soon'))).toBe(1000);
    expect(retryAfterMs(err(429, '600'))).toBe(30000);
    expect(retryAfterMs(err(500, '5'))).toBeNull();
    expect(retryAfterMs(new Error('x'))).toBeNull();
  });
});
