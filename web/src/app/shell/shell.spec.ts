import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { HealthService } from '../core/health.service';
import { KeyboardService } from '../core/keyboard.service';
import { ScopeService } from '../core/scope.service';
import { StatusService } from '../core/status.service';
import { Shell, navItems } from './shell';

const viewer = { email: 'v@example.com', roles: ['viewer'], expiresAt: '2026-11-30T00:00:00Z' };

async function render(me = viewer, health: object | 'none' = { status: 'ok', version: 'abcdef1234', maintenance: false }) {
  TestBed.configureTestingModule({ imports: [Shell], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
  TestBed.inject(AuthService).me.set(me);
  const fixture = TestBed.createComponent(Shell);
  await fixture.whenStable();
  const http = TestBed.inject(HttpTestingController);
  http.expectOne('/api/meta/as-of').flush({ latest: '2026-10-06', dates: ['2026-10-06', '2026-10-05'] });
  http.expectOne('/api/meta/portfolios').flush([
    { portfolioId: 1, name: 'P1', fundId: 10, fundName: 'Fund A' },
    { portfolioId: 2, name: 'P2', fundId: 10, fundName: 'Fund A' },
  ]);
  if (health !== 'none') http.expectOne('/health').flush(health);
  await fixture.whenStable();
  const el = fixture.nativeElement as HTMLElement;
  return { fixture, el, http, q: (id: string) => el.querySelector(`[data-testid=${id}]`) };
}

const key = (init: KeyboardEventInit, target?: EventTarget) => {
  const e = new KeyboardEvent('keydown', { ...init, bubbles: true, cancelable: true });
  (target ?? document).dispatchEvent(e);
  return e;
};

describe('Shell', () => {
  afterEach(() => vi.useRealTimers());

  it('shows the scope controls, the nav without admin pages for a viewer, and the account expiry', async () => {
    const { el, q } = await render();
    expect(el.querySelectorAll('[data-testid=as-of] option').length).toBe(2);
    expect(el.querySelector('[data-testid=as-of] option')?.textContent).toContain('today');
    expect(q('freshness')?.textContent).toContain('06:30 ET');
    expect([...el.querySelectorAll('nav a')].map((a) => a.textContent?.trim().split(' ')[0])).not.toContain('Usage');
    expect(q('expiry')?.textContent).toContain('2026-11-30');
    expect(q('api-state')?.textContent).toContain('Ready');
    expect(el.textContent).toContain('build abcdef1');
    expect(q('rows')).toBeNull();
  });

  it('shows admin pages to admins and no expiry line without one', async () => {
    const { el, q, fixture } = await render({ ...viewer, roles: ['admin'], expiresAt: '' });
    expect(el.querySelectorAll('nav a').length).toBe(navItems.length);
    expect(q('expiry')).toBeNull();
    TestBed.inject(AuthService).me.set(null); // signed out under the shell (session ended)
    await fixture.whenStable();
    expect(q('expiry')).toBeNull();
  });

  it('reports maintenance, waking and unreachable API states', async () => {
    const maint = await render(viewer, { status: 'ok', version: 'v', maintenance: true });
    expect(maint.q('api-state')?.textContent).toContain('Maintenance');
    expect(maint.q('maintenance')).not.toBeNull();
    TestBed.resetTestingModule();

    const waking = await render(viewer, 'none');
    expect(waking.q('api-state')?.textContent).toContain('Waking');
    expect(waking.el.textContent).not.toContain('build');
    vi.useFakeTimers();
    for (let i = 0; i <= HealthService.maxAttempts; i++) {
      waking.http.expectOne('/health').error(new ProgressEvent('error'));
      vi.advanceTimersByTime(HealthService.retryEveryMs);
    }
    waking.fixture.detectChanges();
    expect(waking.q('api-state')?.textContent).toContain('Unreachable');
  });

  it('shows rows, request time, cache status and payload in the status bar', async () => {
    const { fixture, q, el } = await render();
    const status = TestBed.inject(StatusService);
    status.totalRows.set(18342);
    status.lastRequest.set({ ms: 38, cache: 'MISS', bytes: 23756, serverMs: 30 });
    await fixture.whenStable();
    expect(q('rows')?.textContent).toContain('0 visible / 18342 total');
    expect(q('last-request')?.textContent).toContain('38 ms');
    expect(q('x-cache')?.textContent).toContain('MISS');
    expect(el.textContent).toContain('23.2 KB');

    status.visibleRows.set(40);
    status.lastRequest.set({ ms: 2, cache: null, bytes: null, serverMs: null });
    await fixture.whenStable();
    expect(q('rows')?.textContent).toContain('40 visible');
    expect(q('x-cache')).toBeNull();
    expect(el.textContent).not.toContain('KB');
  });

  it('changes as-of, portfolios and account settings', async () => {
    const { fixture, el } = await render();
    const scope = TestBed.inject(ScopeService);
    const select = el.querySelector('[data-testid=as-of]') as HTMLSelectElement;
    select.value = '2026-10-05';
    select.dispatchEvent(new Event('change'));
    expect(scope.asOf()).toBe('2026-10-05');

    expect(el.querySelector('[data-testid=portfolios] summary')?.textContent).toContain('All portfolios');
    (el.querySelector('.check input') as HTMLInputElement).dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(el.querySelector('[data-testid=portfolios] summary')?.textContent).toContain('1 portfolio');
    (el.querySelector('.fund') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(el.querySelector('[data-testid=portfolios] summary')?.textContent).toContain('2 portfolios');

    const toggle = el.querySelector('[data-testid=theme-toggle]') as HTMLButtonElement;
    expect(toggle.getAttribute('aria-label')).toBe('Switch to light theme');
    toggle.click();
    await fixture.whenStable();
    expect(toggle.getAttribute('aria-label')).toBe('Switch to dark theme');

    const [palette, negatives] = [...el.querySelectorAll('.user .check input')] as HTMLInputElement[];
    palette.dispatchEvent(new Event('change'));
    negatives.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    palette.dispatchEvent(new Event('change'));
    negatives.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(toggle).toBeTruthy();
  });

  it('signs out to the login page', async () => {
    const { el, http } = await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    ([...el.querySelectorAll('.user button')].find((b) => b.textContent?.includes('Sign out')) as HTMLButtonElement).click();
    http.expectOne('/api/auth/logout').flush(null);
    expect(navigate).toHaveBeenCalledWith(['/login']);
  });

  it('handles the README §9.3 shortcuts', async () => {
    await render({ ...viewer, roles: ['admin'] });
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const actions = { focusQuickFilter: vi.fn(), exportCsv: vi.fn(), nextPreset: vi.fn() };
    TestBed.inject(KeyboardService).register(actions);

    expect(key({ key: '/' }).defaultPrevented).toBe(true);
    expect(actions.focusQuickFilter).toHaveBeenCalled();
    key({ key: '™', code: 'Digit2', altKey: true }); // macOS Option+2 types "™"; the physical key is what counts
    expect(navigate).toHaveBeenCalledWith(['/', 'funds']);
    key({ key: '6', code: 'Digit6', altKey: true });
    expect(navigate).toHaveBeenCalledWith(['/', 'usage']);
    key({ key: 'E', ctrlKey: true, shiftKey: true });
    expect(actions.exportCsv).toHaveBeenCalled();
    key({ key: 'p', metaKey: true, shiftKey: true });
    expect(actions.nextPreset).toHaveBeenCalled();

    // Typing in a field keeps "/" for the field; plain keys do nothing.
    const input = document.createElement('input');
    document.body.appendChild(input);
    actions.focusQuickFilter.mockClear();
    expect(key({ key: '/' }, input).defaultPrevented).toBe(false);
    expect(actions.focusQuickFilter).not.toHaveBeenCalled();
    input.remove();
    expect(key({ key: 'x' }).defaultPrevented).toBe(false);
  });

  it('ignores shortcuts the page or the role does not support', async () => {
    await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    TestBed.inject(KeyboardService).register({});
    expect(key({ key: '/' }).defaultPrevented).toBe(false);
    expect(key({ key: '6', code: 'Digit6', altKey: true }).defaultPrevented).toBe(false); // Usage is admin-only
    expect(key({ key: '7', code: 'Digit7', altKey: true }).defaultPrevented).toBe(false);
    expect(navigate).not.toHaveBeenCalled();
    expect(key({ key: 'e', ctrlKey: true, shiftKey: true }).defaultPrevented).toBe(false);
    expect(key({ key: 'p', ctrlKey: true, shiftKey: true }).defaultPrevented).toBe(false);
    expect(key({ key: 'e', ctrlKey: true }).defaultPrevented).toBe(false);
  });
});
