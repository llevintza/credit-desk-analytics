import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { HealthService } from '../core/health.service';
import { Login } from './login';

async function render() {
  TestBed.configureTestingModule({ imports: [Login], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
  const fixture = TestBed.createComponent(Login);
  await fixture.whenStable();
  const el = fixture.nativeElement as HTMLElement;
  const http = TestBed.inject(HttpTestingController);
  return { fixture, el, http, q: (id: string) => el.querySelector(`[data-testid=${id}]`) };
}

async function submit(fixture: { whenStable: () => Promise<unknown> }, el: HTMLElement, email: string, password: string) {
  (el.querySelector('[data-testid=email]') as HTMLInputElement).value = email;
  (el.querySelector('[data-testid=password]') as HTMLInputElement).value = password;
  el.querySelector('form')!.dispatchEvent(new Event('submit'));
  await fixture.whenStable();
}

describe('Login', () => {
  afterEach(() => vi.useRealTimers());

  it('shows "Waking the server…" with progress until /health answers', async () => {
    const { fixture, http, q } = await render();
    expect(q('waking')?.textContent).toContain('Waking the server');
    expect((q('waking')!.querySelector('progress') as HTMLProgressElement).value).toBeGreaterThan(0);
    http.expectOne('/health').flush({ status: 'ok', version: 'v' });
    await fixture.whenStable();
    expect(q('waking')).toBeNull();
    expect((fixture.componentInstance as unknown as { progress: () => number }).progress()).toBe(100);
    expect(q('maintenance')).toBeNull();
  });

  it('shows the maintenance banner', async () => {
    const { fixture, http, q } = await render();
    http.expectOne('/health').flush({ status: 'ok', version: 'v', maintenance: true });
    await fixture.whenStable();
    expect(q('maintenance')?.textContent).toContain('maintenance');
  });

  it('says when the server is unreachable', async () => {
    const { fixture, el, http } = await render();
    vi.useFakeTimers();
    for (let i = 0; i <= HealthService.maxAttempts; i++) {
      http.expectOne('/health').error(new ProgressEvent('error'));
      vi.advanceTimersByTime(HealthService.retryEveryMs);
    }
    fixture.detectChanges();
    expect(el.textContent).toContain("isn't answering");
  });

  it('asks for both fields, then signs in and goes to positions', async () => {
    const { fixture, el, http, q } = await render();
    http.expectOne('/health').flush({ status: 'ok', version: 'v' });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    await submit(fixture, el, ' ', '');
    expect(q('login-error')?.textContent).toContain('Enter your email and password');

    await submit(fixture, el, ' a@example.com ', 'pw');
    expect((q('sign-in') as HTMLButtonElement).disabled).toBe(true);
    const req = http.expectOne('/api/auth/login');
    expect(req.request.body).toEqual({ email: 'a@example.com', password: 'pw' });
    req.flush({ email: 'a@example.com', roles: ['viewer'], expiresAt: 'x' });
    expect(navigate).toHaveBeenCalledWith(['/positions']);
  });

  for (const [status, message] of [
    [401, 'Invalid email or password'],
    [429, 'Too many attempts'],
    [503, 'Down for maintenance'],
    [0, 'Could not reach the server'],
  ] as const) {
    it(`explains a ${status} from login`, async () => {
      const { fixture, el, http, q } = await render();
      http.expectOne('/health').flush({ status: 'ok', version: 'v' });
      await submit(fixture, el, 'a@example.com', 'pw');
      const req = http.expectOne('/api/auth/login');
      if (status === 0) req.error(new ProgressEvent('error'));
      else req.flush('no', { status, statusText: 'x' });
      await fixture.whenStable();
      expect(q('login-error')?.textContent).toContain(message);
      expect((q('sign-in') as HTMLButtonElement).disabled).toBe(false);
    });
  }

  it('treats a non-HTTP error like a network failure', async () => {
    const { fixture, el, http, q } = await render();
    http.expectOne('/health').flush({ status: 'ok', version: 'v' });
    const auth = (await import('../core/auth.service')).AuthService;
    const { throwError } = await import('rxjs');
    vi.spyOn(TestBed.inject(auth), 'login').mockReturnValue(throwError(() => new Error('weird')));
    await submit(fixture, el, 'a@example.com', 'pw');
    expect(q('login-error')?.textContent).toContain('Could not reach the server');
  });

  it('copes with a form that has no fields', async () => {
    const { fixture, el, http, q } = await render();
    http.expectOne('/health').flush({ status: 'ok', version: 'v' });
    el.querySelectorAll('input').forEach((i) => i.remove());
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    expect(q('login-error')?.textContent).toContain('Enter your email');
  });
});
