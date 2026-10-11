import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpErrorResponse, HttpXsrfTokenExtractor, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Observable, firstValueFrom } from 'rxjs';
import { ANTIFORGERY_PROBLEM_TITLE, ANTIFORGERY_PROBLEM_TYPE, ANTIFORGERY_URL, XSRF_HEADER, xsrfRefreshInterceptor } from './xsrf-refresh.interceptor';

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
  const rejected = { type: ANTIFORGERY_PROBLEM_TYPE, title: ANTIFORGERY_PROBLEM_TITLE, status: 400 };
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

  it('shares one refresh between concurrent rejections and retries each request', async () => {
    const logout = settle(http().post('/api/auth/logout', null));
    const save = settle(http().put('/api/presets/risk', { name: 'a' }));
    mock().expectOne('/api/auth/logout').flush(rejected, bad);
    mock().expectOne('/api/presets/risk').flush(rejected, bad);
    mock().expectOne(ANTIFORGERY_URL).flush(null, { status: 204, statusText: 'No Content' });
    const retries = [mock().expectOne('/api/auth/logout'), mock().expectOne('/api/presets/risk')];
    for (const retry of retries) {
      expect(retry.request.headers.get(XSRF_HEADER)).toBe('fresh');
      retry.flush(null, { status: 204, statusText: 'No Content' });
    }
    expect(await logout).toBeNull();
    expect(await save).toBeNull();
  });

  it('keeps the shared refresh going for the others when the first caller cancels', async () => {
    const first = http().post('/api/auth/logout', null).subscribe({ error: () => undefined });
    const save = settle(http().put('/api/presets/risk', { name: 'a' }));
    mock().expectOne('/api/auth/logout').flush(rejected, bad);
    mock().expectOne('/api/presets/risk').flush(rejected, bad);
    const refresh = mock().expectOne(ANTIFORGERY_URL);
    first.unsubscribe();
    expect(refresh.cancelled).toBe(false);
    refresh.flush(null, { status: 204, statusText: 'No Content' });
    mock().expectNone('/api/auth/logout');
    const retry = mock().expectOne('/api/presets/risk');
    expect(retry.request.headers.get(XSRF_HEADER)).toBe('fresh');
    retry.flush(null, { status: 204, statusText: 'No Content' });
    expect(await save).toBeNull();
  });

  it('starts a new refresh for a rejection that arrives after the last one failed or succeeded', async () => {
    const failed = settle(http().post('/api/auth/logout', null));
    mock().expectOne('/api/auth/logout').flush(rejected, bad);
    mock().expectOne(ANTIFORGERY_URL).flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(((await failed) as HttpErrorResponse).status).toBe(401);

    for (const url of ['/api/auth/logout', '/api/presets/risk']) {
      const done = settle(http().post(url, null));
      mock().expectOne(url).flush(rejected, bad);
      mock().expectOne(ANTIFORGERY_URL).flush(null, { status: 204, statusText: 'No Content' });
      mock().expectOne(url).flush(null, { status: 204, statusText: 'No Content' });
      expect(await done).toBeNull();
    }
  });

  it('pins the problem type the API sends (AntiforgeryFilter.ProblemType)', () => {
    expect(ANTIFORGERY_PROBLEM_TYPE).toBe('urn:desk:problem:antiforgery');
  });

  it.each([
    ['by its type alone', { type: ANTIFORGERY_PROBLEM_TYPE, title: 'Reworded title', status: 400 }],
    ['by its title alone (the one-release fallback)', { title: ANTIFORGERY_PROBLEM_TITLE, status: 400 }],
  ])('recognises the antiforgery 400 %s and retries', async (_, body) => {
    const done = settle(http().post('/api/auth/logout', null));
    mock().expectOne('/api/auth/logout').flush(body, bad);
    mock().expectOne(ANTIFORGERY_URL).flush(null, { status: 204, statusText: 'No Content' });
    mock().expectOne('/api/auth/logout').flush(null, { status: 204, statusText: 'No Content' });
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

  it('passes the original 400 to the caller when no token came back', async () => {
    token = null;
    const done = settle(http().delete('/api/presets/risk', { headers: { [XSRF_HEADER]: 'stale' } }));
    mock().expectOne('/api/presets/risk').flush(rejected, bad);
    mock().expectOne(ANTIFORGERY_URL).flush(null, { status: 204, statusText: 'No Content' });
    mock().expectNone('/api/presets/risk');
    const e = (await done) as HttpErrorResponse;
    expect(e.status).toBe(400);
    expect(e.error).toEqual(rejected);
  });

  it.each([
    ['another 400 problem', { title: 'One or more validation errors occurred.' }, 400],
    ['a 400 with another problem type', { type: 'urn:desk:problem:other', title: 'Something else' }, 400],
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

  it.each(['https://elsewhere.example/api/x', '//elsewhere.example/api/x', 'api/x', '/apix', '/api-foo'])(
    'never refreshes or retries a URL outside the API (%s)',
    async (url) => {
      const done = settle(http().post(url, {}));
      mock().expectOne(url).flush(rejected, bad);
      mock().expectNone(ANTIFORGERY_URL);
      const e = (await done) as HttpErrorResponse;
      expect(e.status).toBe(400);
      expect(e.error).toEqual(rejected);
    },
  );

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
