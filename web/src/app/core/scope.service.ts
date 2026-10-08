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

  load(): void {
    if (this.dates().length) return;
    forkJoin({ asOf: this.api.asOf(), portfolios: this.api.portfolios() }).subscribe({
      next: ({ asOf, portfolios }) => {
        this.dates.set(asOf.dates);
        this.asOf.set(asOf.latest);
        this.portfolios.set(portfolios);
      },
      error: () => this.error.set(true),
    });
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
