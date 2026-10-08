import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { HealthService } from '../core/health.service';
import { KeyboardService } from '../core/keyboard.service';
import { ScopeService } from '../core/scope.service';
import { StatusService } from '../core/status.service';
import { ThemeService } from '../core/theme.service';

export interface NavItem { path: string; label: string; key: number; admin?: boolean; }

export const navItems: NavItem[] = [
  { path: 'positions', label: 'Positions', key: 1 },
  { path: 'funds', label: 'Fund Performance', key: 2 },
  { path: 'insights', label: 'Insights', key: 3 },
  { path: 'deals', label: 'Deals', key: 4 },
  { path: 'lab', label: 'Performance Lab', key: 5 },
  { path: 'usage', label: 'Usage', key: 6, admin: true },
];

/** The application shell (README §9.2): top bar, left nav, status bar and the global shortcuts (§9.3). */
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'onKey($event)' },
})
export class Shell {
  protected readonly auth = inject(AuthService);
  protected readonly scope = inject(ScopeService);
  protected readonly status = inject(StatusService);
  protected readonly theme = inject(ThemeService);
  private readonly keyboard = inject(KeyboardService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly api = toSignal(inject(HealthService).state(), { initialValue: { kind: 'waking', attempt: 0 } as const });
  protected readonly nav = computed(() => navItems.filter((n) => !n.admin || this.auth.isAdmin()));
  protected readonly apiLabel = computed(() => {
    const s = this.api();
    if (s.kind === 'ready') return s.maintenance ? 'Maintenance' : 'Ready';
    return s.kind === 'waking' ? 'Waking' : 'Unreachable';
  });
  protected readonly version = computed(() => {
    const s = this.api();
    return s.kind === 'ready' ? s.version.slice(0, 7) : null;
  });
  protected readonly portfolioLabel = computed(() => {
    const n = this.scope.selected().length;
    return n === 0 ? 'All portfolios' : `${n} portfolio${n === 1 ? '' : 's'}`;
  });
  protected readonly last = this.status.lastRequest;
  protected readonly expiry = computed(() => this.auth.expiresAt()?.slice(0, 10) ?? null);

  constructor() {
    this.scope.load();
  }

  protected setAsOf(event: Event): void {
    this.scope.asOf.set((event.target as HTMLSelectElement).value);
  }

  /**
   * Any non-401 failure (403, 5xx, network) still signs out locally so the page never looks signed in.
   * The server session may survive until it expires; see the follow-up issue.
   */
  protected logout(): void {
    this.auth.logout().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => void this.router.navigate(['/login']),
      error: (e: unknown) => {
        // 401: sessionInterceptor has already signed out and navigated to /login.
        if (e instanceof HttpErrorResponse && e.status === 401) return;
        this.auth.signedOut();
        void this.router.navigate(['/login']);
      },
    });
  }

  /** README §9.3 shortcuts. Typing in a field keeps its keys, except the Ctrl+Shift combos. */
  protected onKey(e: KeyboardEvent): void {
    const typing = e.target instanceof HTMLElement && (e.target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName));
    const actions = this.keyboard.actions();
    if (e.key === '/' && !typing && actions.focusQuickFilter) {
      e.preventDefault();
      actions.focusQuickFilter();
    } else if (e.altKey && /^Digit[1-6]$/.test(e.code)) {
      // e.code, not e.key: on macOS Option+2 types "™", but the physical key is still Digit2.
      const item = this.nav().find((n) => n.key === Number(e.code.slice(5)));
      if (item) {
        e.preventDefault();
        void this.router.navigate(['/', item.path]);
      }
    } else if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key.toLowerCase() === 'e' && actions.exportCsv) {
      e.preventDefault();
      actions.exportCsv();
    } else if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key.toLowerCase() === 'p' && actions.nextPreset) {
      e.preventDefault();
      actions.nextPreset();
    }
  }
}
