import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { SlowRequest, UsageReport } from '../data-access/api.types';
import { UsagePage, countText, ratioText, timeText, windows } from './usage';

const slow = (id: number, day: string, ms: number, extra: Partial<SlowRequest> = {}): SlowRequest => ({
  id, day, at: `${day}T10:15:0${id % 10}Z`, userName: 'viewer@example.com', endpoint: 'POST /api/positions/query', status: 200, rows: 500, ms, cache: 'MISS', ...extra,
});

const report = (over: Partial<UsageReport> = {}): UsageReport => ({
  from: '2026-10-10', to: '2026-10-16', requests: 1234, hits: 900, misses: 300, hitRatio: 0.75,
  days: [
    { day: '2026-10-16', requests: 1000, hits: 800, misses: 200, hitRatio: 0.8 },
    { day: '2026-10-15', requests: 234, hits: 100, misses: 100, hitRatio: 0.5 },
  ],
  users: [
    { day: '2026-10-16', userName: 'viewer@example.com', requests: 1000, hits: 800, misses: 200, errors: 2, avgMs: 31 },
    { day: '2026-10-15', userName: null, requests: 234, hits: 100, misses: 100, errors: 0, avgMs: 12 },
  ],
  slowest: [slow(3, '2026-10-16', 2500, { status: 503, cache: null, rows: null, userName: null }), slow(2, '2026-10-16', 900), slow(1, '2026-10-15', 400)],
  ...over,
});

describe('Usage page (README §7.2, #70)', () => {
  async function render() {
    TestBed.configureTestingModule({ imports: [UsagePage], providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(UsagePage);
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid=${id}]`);
    const usageReq = (): TestRequest => http.expectOne((r) => r.url === '/api/admin/usage');
    const settle = () => fixture.whenStable();
    return { fixture, http, el, q, usageReq, settle };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('loads the last 7 days and shows totals, per-day, per-user and the newest day\'s slowest', async () => {
    const { el, q, usageReq, settle } = await render();
    const req = usageReq();
    expect(req.request.params.get('days')).toBe('7');
    expect(el.querySelector('.skeleton')).not.toBeNull();
    req.flush(report());
    await settle();

    expect(el.querySelector('.skeleton')).toBeNull();
    expect(q('caption')?.textContent).toContain('2026-10-10 to 2026-10-16');
    expect(q('kpi-requests')?.textContent).toBe('1,234');
    expect(q('kpi-ratio')?.textContent).toBe('75.0%');
    expect(q('kpi-hits')?.textContent).toContain('900 / 300');
    expect(q('window-7')?.getAttribute('aria-pressed')).toBe('true');

    const dayRows = q('days')!.querySelectorAll('tbody tr');
    expect(dayRows.length).toBe(2);
    expect(dayRows[1].textContent).toContain('50.0%');

    const userRows = q('users')!.querySelectorAll('tbody tr');
    expect(userRows[0].textContent).toContain('viewer@example.com');
    expect(userRows[0].querySelector('.neg')?.textContent).toBe('2'); // 5xx stand out
    expect(userRows[1].textContent).toContain('—'); // no user name

    const slowRows = q('slowest')!.querySelectorAll('tbody tr');
    expect(slowRows.length).toBe(2); // only 2026-10-16
    expect(slowRows[0].textContent).toContain('2,500');
    expect(slowRows[0].textContent).toContain('10:15:03');
    expect(slowRows[0].querySelector('.neg')?.textContent).toBe('503');
    expect((q('slow-day') as HTMLSelectElement).value).toBe('2026-10-16');
  });

  it('switches the slowest table to another day', async () => {
    const { q, usageReq, settle } = await render();
    usageReq().flush(report());
    await settle();

    const select = q('slow-day') as HTMLSelectElement;
    select.value = '2026-10-15';
    select.dispatchEvent(new Event('change'));
    await settle();

    const rows = q('slowest')!.querySelectorAll('tbody tr');
    expect(rows.length).toBe(1);
    expect(rows[0].textContent).toContain('400');
  });

  it('a new window supersedes the request in flight; only the last one is shown', async () => {
    const { http, q, settle } = await render();
    const first = http.expectOne((r) => r.url === '/api/admin/usage' && r.params.get('days') === '7');
    q('window-30')!.click();
    await settle();
    expect(first.cancelled).toBe(true);
    http.expectOne((r) => r.url === '/api/admin/usage' && r.params.get('days') === '30').flush(report({ from: '2026-09-17' }));
    await settle();
    expect(q('caption')?.textContent).toContain('2026-09-17');
    expect(q('window-30')?.getAttribute('aria-pressed')).toBe('true');
    expect(q('window-1')?.textContent).toContain('Today');
  });

  it('shows an empty window without tables and a dash for the hit ratio', async () => {
    const { q, usageReq, settle } = await render();
    usageReq().flush(report({ requests: 0, hits: 0, misses: 0, hitRatio: null, days: [], users: [], slowest: [] }));
    await settle();
    expect(q('kpi-ratio')?.textContent).toBe('—');
    expect(q('days-empty')).not.toBeNull();
    expect(q('users')).toBeNull();
    expect(q('slowest')).toBeNull();
    expect(q('slow-day')).toBeNull();
  });

  it('shows an error with a retry that asks again', async () => {
    const { el, q, usageReq, settle } = await render();
    usageReq().flush('boom', { status: 500, statusText: 'x' });
    await settle();
    expect(el.querySelector('[role=alert]')?.textContent).toContain('Could not load usage');
    q('retry')!.click();
    await settle();
    usageReq().flush(report());
    await settle();
    expect(q('usage')).not.toBeNull();

    q('refresh')!.click(); // refresh asks again too
    await settle();
    usageReq().flush(report());
  });

  it('clears the server caches and says so, or says it failed', async () => {
    const { http, q, usageReq, settle } = await render();
    usageReq().flush(report());
    await settle();

    q('clear-cache')!.click();
    await settle();
    expect((q('clear-cache') as HTMLButtonElement).disabled).toBe(true);
    const clear = http.expectOne('/api/admin/cache/clear');
    expect(clear.request.method).toBe('POST');
    clear.flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    expect(q('clear-status')?.textContent).toContain('cleared');

    q('clear-cache')!.click();
    await settle();
    http.expectOne('/api/admin/cache/clear').flush('no', { status: 403, statusText: 'Forbidden' });
    await settle();
    expect(q('clear-status')?.textContent).toContain('Could not clear');
  });
});

describe('Usage formatting', () => {
  it('formats ratios, counts and times', () => {
    expect(ratioText(null)).toBe('—');
    expect(ratioText(2 / 3)).toBe('66.7%');
    expect(countText(null)).toBe('');
    expect(countText(12345)).toBe('12,345');
    expect(timeText('2026-10-16T23:59:58.123+00:00')).toBe('23:59:58');
    expect(windows).toEqual([1, 7, 30]);
  });
});
