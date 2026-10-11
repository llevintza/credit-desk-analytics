import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { API_BASE_URL, apiBaseInterceptor } from './api-base';
import { REFRESH_URL, authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';
import { sessionInterceptor } from './session.interceptor';
import { TokenStore, Tokens } from './token.store';

const pair = (n: number): Tokens => ({ tokenType: 'Bearer', accessToken: `access-${n}`, expiresIn: 300, refreshToken: `refresh-${n}` });
const me = { email: 'a@example.com', roles: ['viewer'], expiresAt: 'x' };
const unauthorized = { status: 401, statusText: 'Unauthorized' };

describe('authInterceptor and the token refresh (ADR-0024)', () => {
  let ctl: HttpTestingController;
  let http: HttpClient;
  let tokens: TokenStore;

  function setup(base = '') {
    TestBed.configureTestingModule({
      providers: [
        // The app's chain (app.config.ts), so a refused refresh reaches sessionInterceptor as it does in the app.
        provideHttpClient(withInterceptors([sessionInterceptor, authInterceptor, apiBaseInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: API_BASE_URL, useValue: base },
      ],
    });
    ctl = TestBed.inject(HttpTestingController);
    http = TestBed.inject(HttpClient);
    tokens = TestBed.inject(TokenStore);
  }

  const get = (url = '/api/meta/columns') => firstValueFrom(http.get(url)).catch((e: unknown) => e);
  const bearer = (req: TestRequest) => req.request.headers.get('Authorization');
  const refresh = () => {
    const req = ctl.expectOne(REFRESH_URL);
    expect(req.request.method).toBe('POST');
    expect(bearer(req)).toBeNull();
    return req;
  };

  afterEach(() => {
    ctl.verify();
    sessionStorage.clear();
  });

  it('adds the access token to API calls only', async () => {
    setup();
    tokens.set(pair(1));
    const calls = [get('/api/me'), get('/health'), get('favicon.ico')];
    const api = ctl.expectOne('/api/me');
    expect(bearer(api)).toBe('Bearer access-1');
    api.flush(me);
    const health = ctl.expectOne('/health');
    const icon = ctl.expectOne('favicon.ico');
    expect(bearer(health)).toBeNull();
    expect(bearer(icon)).toBeNull();
    health.flush({});
    icon.flush('');
    await Promise.all(calls);
  });

  it('never sends a bearer token with the login or refresh calls themselves', async () => {
    setup();
    tokens.set(pair(1));
    const login = firstValueFrom(http.post('/api/auth/login', {}));
    const req = ctl.expectOne('/api/auth/login');
    expect(bearer(req)).toBeNull();
    req.flush(pair(2));
    await login;
  });

  it('sends no token, and refreshes nothing, when signed out', async () => {
    setup();
    const res = get();
    const req = ctl.expectOne('/api/meta/columns');
    expect(bearer(req)).toBeNull();
    req.flush('no', unauthorized);
    expect((await res) as HttpErrorResponse).toMatchObject({ status: 401 });
  });

  it('after a reload, refreshes first with the token the tab kept, then sends', async () => {
    sessionStorage.setItem('desk.refresh', 'refresh-0'); // before the store is created, as on a reload
    setup('https://api.example.test');
    const res = get();
    const r = ctl.expectOne(`https://api.example.test${REFRESH_URL}`);
    expect(r.request.body).toEqual({ refreshToken: 'refresh-0' });
    r.flush(pair(1));
    const req = ctl.expectOne('https://api.example.test/api/meta/columns');
    expect(bearer(req)).toBe('Bearer access-1');
    req.flush(['ok']);
    expect(await res).toEqual(['ok']);
    expect(sessionStorage.getItem('desk.refresh')).toBe('refresh-1');
  });

  it('refreshes once on a 401 and retries with the new token', async () => {
    setup();
    tokens.set(pair(1));
    const res = get();
    ctl.expectOne('/api/meta/columns').flush('expired', unauthorized);
    const r = refresh();
    expect(r.request.body).toEqual({ refreshToken: 'refresh-1' });
    r.flush(pair(2));
    const retry = ctl.expectOne('/api/meta/columns');
    expect(bearer(retry)).toBe('Bearer access-2');
    retry.flush(['ok']);
    expect(await res).toEqual(['ok']);
  });

  it('does not refresh twice when the retry is refused too', async () => {
    setup();
    tokens.set(pair(1));
    const res = get();
    ctl.expectOne('/api/meta/columns').flush('expired', unauthorized);
    refresh().flush(pair(2));
    ctl.expectOne('/api/meta/columns').flush('still no', unauthorized);
    expect((await res) as HttpErrorResponse).toMatchObject({ status: 401 });
    ctl.expectNone(REFRESH_URL);
  });

  it('signs out when the refresh is refused: tokens gone, back to /login', async () => {
    setup();
    tokens.set(pair(1));
    const auth = TestBed.inject(AuthService);
    auth.me.set(me);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const res = get();
    ctl.expectOne('/api/meta/columns').flush('expired', unauthorized);
    refresh().flush('session ended', unauthorized);
    expect((await res) as HttpErrorResponse).toMatchObject({ status: 401 });
    expect(tokens.hasSession()).toBe(false);
    expect(sessionStorage.getItem('desk.refresh')).toBeNull();
    expect(auth.me()).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/login']);
  });

  it('keeps the session through a refresh that fails for another reason (a cold start, a 503)', async () => {
    setup();
    tokens.set(pair(1));
    const auth = TestBed.inject(AuthService);
    auth.me.set(me);
    const res = get();
    ctl.expectOne('/api/meta/columns').flush('expired', unauthorized);
    refresh().flush('maintenance', { status: 503, statusText: 'Service Unavailable' });
    expect((await res) as HttpErrorResponse).toMatchObject({ status: 503 });
    expect(tokens.refreshToken()).toBe('refresh-1');
    expect(auth.me()).toEqual(me);
  });

  it('shares one refresh between concurrent 401s', async () => {
    setup();
    tokens.set(pair(1));
    const a = get('/api/meta/columns');
    const b = get('/api/meta/as-of');
    ctl.expectOne('/api/meta/columns').flush('expired', unauthorized);
    ctl.expectOne('/api/meta/as-of').flush('expired', unauthorized);
    refresh().flush(pair(2));
    const retries = [ctl.expectOne('/api/meta/columns'), ctl.expectOne('/api/meta/as-of')];
    expect(retries.map(bearer)).toEqual(['Bearer access-2', 'Bearer access-2']);
    retries.forEach((r) => r.flush(['ok']));
    expect(await Promise.all([a, b])).toEqual([['ok'], ['ok']]);
  });

  it('retries a late 401 with the token a sibling already got, without another refresh', async () => {
    setup();
    tokens.set(pair(1));
    const res = get();
    const first = ctl.expectOne('/api/meta/columns');
    tokens.set(pair(2)); // a sibling's refresh finished while this request was in flight
    first.flush('expired', unauthorized);
    ctl.expectNone(REFRESH_URL);
    const retry = ctl.expectOne('/api/meta/columns');
    expect(bearer(retry)).toBe('Bearer access-2');
    retry.flush(['ok']);
    expect(await res).toEqual(['ok']);
  });

  it('passes other errors through', async () => {
    setup();
    tokens.set(pair(1));
    const failed = get();
    ctl.expectOne('/api/meta/columns').flush('boom', { status: 500, statusText: 'Server Error' });
    expect((await failed) as HttpErrorResponse).toMatchObject({ status: 500 });
    ctl.expectNone(REFRESH_URL);
  });

  it('does not refresh again for a 401 that lands after a sibling refresh was refused', async () => {
    setup();
    tokens.set(pair(1));
    const first = get('/api/meta/columns');
    const late = get('/api/meta/as-of');
    ctl.expectOne('/api/meta/columns').flush('expired', unauthorized);
    refresh().flush('session ended', unauthorized);
    expect((await first) as HttpErrorResponse).toMatchObject({ status: 401 });
    ctl.expectOne('/api/meta/as-of').flush('expired', unauthorized);
    expect((await late) as HttpErrorResponse).toMatchObject({ status: 401 });
    ctl.expectNone(REFRESH_URL);
  });
});
