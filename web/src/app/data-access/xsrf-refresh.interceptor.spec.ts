import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpErrorResponse, HttpXsrfTokenExtractor, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Observable, firstValueFrom } from 'rxjs';
import { ANTIFORGERY_PROBLEM_TITLE, ANTIFORGERY_URL, XSRF_HEADER, xsrfRefreshInterceptor } from './xsrf-refresh.interceptor';

describe('xsrfRefreshInterceptor', () => {
  let token: string | null;

  beforeEach(() => {
    token = 'fresh';
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([xsrfRefreshInterceptor])),
        provideHttpClientTesting(),
        { provide: HttpXsrfTokenExtractor, useValue: { getToken: () => token } },
      ],
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  const http = () => TestBed.inject(HttpClient);
  const mock = () => TestBed.inject(HttpTestingController);
  const settle = <T>(o: Observable<T>) => firstValueFrom(o).catch((e: unknown) => e);
  const rejected = { title: ANTIFORGERY_PROBLEM_TITLE, status: 400 };
  const bad = { status: 400, statusText: 'Bad Request' };
  /** Lets the interceptor's promise-based steps (reading a Blob problem) run. */
  const tick = () => new Promise((r) => setTimeout(r));

  it('refreshes the token once and retries the unsafe request with it', async () => {
    const done = settle(http().post('/api/auth/logout', null, { headers: { [XSRF_HEADER]: 'stale' } }));
    mock().expectOne('/api/auth/logout').flush(rejected, bad);
    const refresh = mock().expectOne(ANTIFORGERY_URL);
    expect(refresh.request.method).toBe('GET');
    refresh.flush(null, { status: 204, statusText: 'No Content' });
    const retry = mock().expectOne('/api/auth/logout');
    expect(retry.request.headers.get(XSRF_HEADER)).toBe('fresh');
    retry.flush(null, { status: 204, statusText: 'No Content' });
    expect(await done).toBeNull();
  });

  it('gives up after one retry: a second rejection goes to the caller without another refresh', async () => {
    const done = settle(http().put('/api/presets/risk', { name: 'a' }));
    mock().expectOne('/api/presets/risk').flush(rejected, bad);
    mock().expectOne(ANTIFORGERY_URL).flush(null, { status: 204, statusText: 'No Content' });
    mock().expectOne('/api/presets/risk').flush(rejected, bad);
    const e = (await done) as HttpErrorResponse;
    expect(e.status).toBe(400);
    expect(e.error).toEqual(rejected);
    mock().expectNone(ANTIFORGERY_URL);
  });

  it('passes a failed refresh to the caller and does not retry', async () => {
    const done = settle(http().post('/api/positions/query', {}));
    mock().expectOne('/api/positions/query').flush(rejected, bad);
    mock().expectOne(ANTIFORGERY_URL).flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(((await done) as HttpErrorResponse).status).toBe(401);
  });

  it('retries with the request as it was when no token came back', async () => {
    token = null;
    const done = settle(http().delete('/api/presets/risk', { headers: { [XSRF_HEADER]: 'stale' } }));
    mock().expectOne('/api/presets/risk').flush(rejected, bad);
    mock().expectOne(ANTIFORGERY_URL).flush(null, { status: 204, statusText: 'No Content' });
    const retry = mock().expectOne('/api/presets/risk');
    expect(retry.request.headers.get(XSRF_HEADER)).toBe('stale');
    retry.flush(null, { status: 204, statusText: 'No Content' });
    expect(await done).toBeNull();
  });

  it.each([
    ['another 400 problem', { title: 'One or more validation errors occurred.' }, 400],
    ['a 400 with no body', null, 400],
    ['a 400 with a text body', 'bad', 400],
    ['a 403', rejected, 403],
    ['a 500', rejected, 500],
  ])('does not retry %s', async (_, body, status) => {
    const done = settle(http().post('/api/positions/query', {}));
    mock().expectOne('/api/positions/query').flush(body, { status, statusText: 'x' });
    expect(((await done) as HttpErrorResponse).status).toBe(status);
  });

  it('does not retry a network failure', async () => {
    const done = settle(http().post('/api/positions/query', {}));
    mock().expectOne('/api/positions/query').error(new ProgressEvent('error'));
    expect(((await done) as HttpErrorResponse).status).toBe(0);
  });

  it.each(['GET', 'HEAD', 'OPTIONS'])('leaves %s requests alone', async (method) => {
    const done = settle(http().request(method, '/api/me'));
    mock().expectOne('/api/me').flush(rejected, bad);
    expect(((await done) as HttpErrorResponse).status).toBe(400);
  });

  it('reads the problem of a blob request (the CSV export) and retries it', async () => {
    const done = settle(http().post('/api/positions/export', {}, { responseType: 'blob' }));
    mock().expectOne('/api/positions/export').flush(new Blob([JSON.stringify(rejected)]), bad);
    await tick();
    mock().expectOne(ANTIFORGERY_URL).flush(null, { status: 204, statusText: 'No Content' });
    mock().expectOne('/api/positions/export').flush(new Blob(['a,b']));
    expect(await (await done as Blob).text()).toBe('a,b');
  });

  it('does not retry a blob request whose 400 is not JSON', async () => {
    const done = settle(http().post('/api/positions/export', {}, { responseType: 'blob' }));
    mock().expectOne('/api/positions/export').flush(new Blob(['<html>']), bad);
    await tick();
    expect(((await done) as HttpErrorResponse).status).toBe(400);
  });
});
