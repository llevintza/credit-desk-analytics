import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { HealthService } from '../core/health.service';

/**
 * Sign-in (README §9.2): minimal, with a "Waking the server…" progress state while the free-tier instance
 * cold-starts (it polls /health), and the maintenance banner.
 */
@Component({
  selector: 'app-login',
  templateUrl: './login.html',
  styleUrl: './login.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly api = toSignal(inject(HealthService).state(), { initialValue: { kind: 'waking', attempt: 0 } as const });
  protected readonly waking = computed(() => this.api().kind === 'waking');
  protected readonly progress = computed(() => {
    const s = this.api();
    return s.kind === 'waking' ? Math.min(100, Math.round(((s.attempt + 1) / HealthService.maxAttempts) * 100)) : 100;
  });
  protected readonly maintenance = computed(() => {
    const s = this.api();
    return s.kind === 'ready' && s.maintenance;
  });
  protected readonly unreachable = computed(() => this.api().kind === 'unreachable');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected submit(event: Event): void {
    event.preventDefault();
    const form = new FormData(event.target as HTMLFormElement);
    const email = String(form.get('email') ?? '').trim();
    const password = String(form.get('password') ?? '');
    if (!email || !password) {
      this.error.set('Enter your email and password.');
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.auth.login(email, password).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => void this.router.navigate(['/positions']),
      error: (e: unknown) => {
        this.busy.set(false);
        const status = e instanceof HttpErrorResponse ? e.status : 0;
        this.error.set(
          status === 429 ? 'Too many attempts. Wait a minute and try again.'
          : status === 503 ? 'Down for maintenance. Please try again later.'
          : status === 401 ? 'Invalid email or password, or the account is locked, disabled or expired.'
          : 'Could not reach the server. Try again.');
      },
    });
  }
}
