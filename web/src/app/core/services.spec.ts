import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { AuthService, adminGuard, authGuard } from './auth.service';
import { KeyboardService } from './keyboard.service';
import { ScopeService } from './scope.service';
import { StatusService } from './status.service';
import { ThemeService } from './theme.service';

const me = { email: 'a@example.com', roles: ['admin'], expiresAt: '2026-12-01T00:00:00Z' };

function setup() {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
  return TestBed.inject(HttpTestingController);
}

describe('AuthService and guards', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('checks the session once and exposes role and expiry', async () => {
    const http = setup();
    const auth = TestBed.inject(AuthService);
    expect(auth.isAdmin()).toBe(false);
    expect(auth.expiresAt()).toBeNull();
    const ok = firstValueFrom(auth.check());
    http.expectOne('/api/me').flush(me);
    expect(await ok).toBe(true);
    expect(auth.isAdmin()).toBe(true);
    expect(auth.expiresAt()).toBe(me.expiresAt);
    expect(await firstValueFrom(auth.check())).toBe(true); // cached: no second request
  });

  it('treats 401 as signed out and rethrows other errors', async () => {
    const http = setup();
    const auth = TestBed.inject(AuthService);
    const out = firstValueFrom(auth.check());
    http.expectOne('/api/me').flush('no', { status: 401, statusText: 'Unauthorized' });
    expect(await out).toBe(false);
    expect(await firstValueFrom(auth.check())).toBe(false);

    auth.me.set(undefined);
    const broken = firstValueFrom(auth.check());
    http.expectOne('/api/me').flush('boom', { status: 500, statusText: 'Server Error' });
    await expect(broken).rejects.toBeTruthy();
  });

  it('logs in and out', async () => {
    const http = setup();
    const auth = TestBed.inject(AuthService);
    const login = firstValueFrom(auth.login('a@example.com', 'pw'));
    const req = http.expectOne('/api/auth/login');
    expect(req.request.body).toEqual({ email: 'a@example.com', password: 'pw' });
    req.flush(me);
    await login;
    expect(auth.me()).toEqual(me);
    const scope = TestBed.inject(ScopeService);
    scope.selected.set([7]);
    scope.portfolios.set([{ portfolioId: 7, name: 'P7', fundId: 1, fundName: 'F' }]);
    const logout = firstValueFrom(auth.logout(), { defaultValue: undefined });
    http.expectOne('/api/auth/logout').flush(null);
    await logout;
    expect(auth.me()).toBeNull();
    // The next account must not inherit this one's entitlement-scoped portfolios or selection.
    expect(scope.selected()).toEqual([]);
    expect(scope.portfolios()).toEqual([]);
  });

  it('authGuard lets a session through and sends everyone else to /login', async () => {
    const http = setup();
    const run = () => TestBed.runInInjectionContext(() => authGuard({} as never, {} as never)) as Observable<boolean | UrlTree>;
    const denied = firstValueFrom(run());
    http.expectOne('/api/me').flush('no', { status: 401, statusText: 'Unauthorized' });
    expect(TestBed.inject(Router).serializeUrl((await denied) as UrlTree)).toBe('/login');

    TestBed.inject(AuthService).me.set(me);
    expect(await firstValueFrom(run())).toBe(true);

    // A cold-starting server (502) also goes to /login, whose "Waking the server…" state waits for it.
    TestBed.inject(AuthService).me.set(undefined);
    const cold = firstValueFrom(run());
    http.expectOne('/api/me').flush('starting', { status: 502, statusText: 'Bad Gateway' });
    expect(TestBed.inject(Router).serializeUrl((await cold) as UrlTree)).toBe('/login');
  });

  it('adminGuard keeps viewers out', () => {
    setup();
    const auth = TestBed.inject(AuthService);
    auth.me.set({ ...me, roles: ['viewer'] });
    const result = TestBed.runInInjectionContext(() => adminGuard({} as never, {} as never));
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/positions');
    auth.me.set(me);
    expect(TestBed.runInInjectionContext(() => adminGuard({} as never, {} as never))).toBe(true);
  });
});

describe('ScopeService', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  const portfolios = [
    { portfolioId: 1, name: 'P1', fundId: 10, fundName: 'Fund A' },
    { portfolioId: 2, name: 'P2', fundId: 10, fundName: 'Fund A' },
    { portfolioId: 3, name: 'P3', fundId: 20, fundName: 'Fund B' },
  ];

  it('loads dates and portfolios once, groups by fund', () => {
    const http = setup();
    const scope = TestBed.inject(ScopeService);
    expect(scope.isLatest()).toBe(false);
    expect(scope.ready()).toBe(false);
    scope.load();
    scope.load(); // the shell and the page both ask: one request
    http.expectOne('/api/meta/as-of').flush({ latest: '2026-10-06', dates: ['2026-10-06', '2026-10-05'] });
    http.expectOne('/api/meta/portfolios').flush(portfolios);
    expect(scope.asOf()).toBe('2026-10-06');
    expect(scope.ready()).toBe(true);
    expect(scope.isLatest()).toBe(true);
    expect(scope.funds().map((f) => [f.fundName, f.portfolios.length])).toEqual([['Fund A', 2], ['Fund B', 1]]);
    scope.load(); // already loaded
    scope.asOf.set('2026-10-05');
    expect(scope.isLatest()).toBe(false);
  });

  it('reports a load error', () => {
    const http = setup();
    const scope = TestBed.inject(ScopeService);
    scope.load();
    http.expectOne('/api/meta/as-of').flush('boom', { status: 500, statusText: 'x' });
    http.match('/api/meta/portfolios'); // cancelled by forkJoin once as-of fails; just take it off the queue
    expect(scope.error()).toBe(true);
    expect(scope.ready()).toBe(true); // pages can still start (without an as-of: the API uses the latest)
    scope.reset();
    expect([scope.error(), scope.ready()]).toEqual([false, false]);
    scope.load(); // a reset allows a new load
    http.expectOne('/api/meta/as-of').flush({ latest: 'x', dates: ['x'] });
    http.expectOne('/api/meta/portfolios').flush([]);
  });

  it('cancels an in-flight load on reset: a late response cannot restore the previous user\'s scope', () => {
    const http = setup();
    const scope = TestBed.inject(ScopeService);
    scope.load();
    const stale = [http.expectOne('/api/meta/as-of'), http.expectOne('/api/meta/portfolios')];
    scope.reset(); // sign-out while the previous user's load is in flight
    expect(stale.map((r) => r.cancelled)).toEqual([true, true]);
    expect([scope.dates(), scope.portfolios(), scope.asOf()]).toEqual([[], [], null]);

    scope.load(); // the next user's load goes out and lands
    http.expectOne('/api/meta/as-of').flush({ latest: '2026-10-05', dates: ['2026-10-05'] });
    http.expectOne('/api/meta/portfolios').flush([portfolios[2]]);
    expect([scope.dates(), scope.portfolios()]).toEqual([['2026-10-05'], [portfolios[2]]]);
  });

  it('toggles portfolios and whole funds', () => {
    setup();
    const scope = TestBed.inject(ScopeService);
    scope.portfolios.set(portfolios);
    scope.toggle(3);
    scope.toggle(1);
    expect(scope.selected()).toEqual([1, 3]);
    scope.toggle(3);
    expect(scope.selected()).toEqual([1]);
    scope.toggleFund(10); // part-selected → select all of it
    expect(scope.selected()).toEqual([1, 2]);
    scope.toggleFund(10); // all selected → clear it
    expect(scope.selected()).toEqual([]);
  });
});

describe('ThemeService', () => {
  beforeEach(() => localStorage.clear()); // other specs may have saved a theme
  afterEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('starts dark, toggles, and persists to storage and <html>', () => {
    TestBed.configureTestingModule({});
    const theme = TestBed.inject(ThemeService);
    expect(theme.theme()).toBe('dark');
    theme.toggleTheme();
    theme.palette.set('colorblind');
    TestBed.tick();
    expect(document.documentElement.dataset['theme']).toBe('light');
    expect(document.documentElement.dataset['palette']).toBe('colorblind');
    expect(JSON.parse(localStorage.getItem('desk.settings')!)).toEqual({ theme: 'light', palette: 'colorblind', negatives: 'minus' });
    theme.toggleTheme();
    expect(theme.theme()).toBe('dark');
  });

  it('reads saved settings and falls back when storage is broken', () => {
    localStorage.setItem('desk.settings', JSON.stringify({ theme: 'light', negatives: 'parens' }));
    TestBed.configureTestingModule({});
    const saved = TestBed.inject(ThemeService);
    expect([saved.theme(), saved.palette(), saved.negatives()]).toEqual(['light', 'standard', 'parens']);

    TestBed.resetTestingModule();
    localStorage.setItem('desk.settings', '{not json');
    TestBed.configureTestingModule({});
    expect(TestBed.inject(ThemeService).theme()).toBe('dark');

    TestBed.resetTestingModule();
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    TestBed.configureTestingModule({});
    const blocked = TestBed.inject(ThemeService);
    blocked.toggleTheme();
    expect(() => TestBed.tick()).not.toThrow();
    expect(blocked.theme()).toBe('light');
  });
});

describe('StatusService and KeyboardService', () => {
  it('status resets', () => {
    TestBed.configureTestingModule({});
    const status = TestBed.inject(StatusService);
    status.totalRows.set(5);
    status.visibleRows.set(2);
    status.lastRequest.set({ ms: 1, cache: 'HIT', bytes: 1, serverMs: 1 });
    status.reset();
    expect([status.totalRows(), status.visibleRows(), status.lastRequest()]).toEqual([null, null, null]);
  });

  it('keyboard actions register and clear', () => {
    TestBed.configureTestingModule({});
    const keys = TestBed.inject(KeyboardService);
    const exportCsv = vi.fn();
    keys.register({ exportCsv });
    keys.actions().exportCsv?.();
    expect(exportCsv).toHaveBeenCalled();
    keys.clear();
    expect(keys.actions()).toEqual({});
  });
});
