import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';
import { sessionInterceptor } from './session.interceptor';

describe('sessionInterceptor', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([sessionInterceptor])), provideHttpClientTesting(), provideRouter([])],
    });
  });

  const call = (url: string, status: number) => {
    const done = firstValueFrom(TestBed.inject(HttpClient).get(url)).catch((e: unknown) => e);
    TestBed.inject(HttpTestingController).expectOne(url).flush('x', { status, statusText: 'x' });
    return done;
  };

  it('sends a signed-in user whose session ended back to the login page', async () => {
    const auth = TestBed.inject(AuthService);
    auth.me.set({ email: 'a@example.com', roles: ['viewer'], expiresAt: 'x' });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await call('/api/positions/query', 401);
    expect(auth.me()).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/login']);
  });

  it('leaves other errors, the session check, login and signed-out users alone', async () => {
    const auth = TestBed.inject(AuthService);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    auth.me.set({ email: 'a@example.com', roles: ['viewer'], expiresAt: 'x' });
    await call('/api/positions/query', 500);
    await call('/api/me', 401);
    await call('/api/auth/login', 401);
    expect(navigate).not.toHaveBeenCalled();
    auth.me.set(null);
    await call('/api/meta/columns', 401);
    expect(navigate).not.toHaveBeenCalled();
  });
});
