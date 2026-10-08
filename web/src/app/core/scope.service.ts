import { Injectable, computed, inject, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import { Portfolio } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';

/** The shell's scope controls (README §9.2): as-of date and the fund/portfolio selection, shared by every page. */
@Injectable({ providedIn: 'root' })
export class ScopeService {
  private readonly api = inject(DeskApi);

  readonly dates = signal<string[]>([]);
  readonly asOf = signal<string | null>(null);
  readonly portfolios = signal<Portfolio[]>([]);
  /** Empty means "all entitled portfolios". */
  readonly selected = signal<number[]>([]);
  readonly isLatest = computed(() => this.asOf() !== null && this.asOf() === this.dates()[0]);
  readonly funds = computed(() => {
    const byFund = new Map<number, { fundId: number; fundName: string; portfolios: Portfolio[] }>();
    for (const p of this.portfolios()) {
      const f = byFund.get(p.fundId) ?? { fundId: p.fundId, fundName: p.fundName, portfolios: [] };
      f.portfolios.push(p);
      byFund.set(p.fundId, f);
    }
    return [...byFund.values()];
  });
  readonly error = signal(false);
  /** True once dates are known or loading failed: pages can start their first request. */
  readonly ready = computed(() => this.asOf() !== null || this.error());
  private loading = false;

  /** Idempotent: the shell and the first page may both ask; one request goes out. */
  load(): void {
    if (this.loading || this.dates().length) return;
    this.loading = true;
    forkJoin({ asOf: this.api.asOf(), portfolios: this.api.portfolios() }).subscribe({
      next: ({ asOf, portfolios }) => {
        this.loading = false;
        this.dates.set(asOf.dates);
        this.asOf.set(asOf.latest);
        this.portfolios.set(portfolios);
      },
      error: () => {
        this.loading = false;
        this.error.set(true);
      },
    });
  }

  /** Forget everything (sign-out / a new account): the next load reads this user's entitlements. */
  reset(): void {
    this.loading = false;
    this.dates.set([]);
    this.asOf.set(null);
    this.portfolios.set([]);
    this.selected.set([]);
    this.error.set(false);
  }

  toggle(portfolioId: number): void {
    this.selected.update((s) => (s.includes(portfolioId) ? s.filter((x) => x !== portfolioId) : [...s, portfolioId].sort((a, b) => a - b)));
  }

  toggleFund(fundId: number): void {
    const ids = this.portfolios().filter((p) => p.fundId === fundId).map((p) => p.portfolioId);
    const all = ids.every((id) => this.selected().includes(id));
    this.selected.update((s) => (all ? s.filter((x) => !ids.includes(x)) : [...new Set([...s, ...ids])].sort((a, b) => a - b)));
  }
}
