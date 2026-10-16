import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, map, of, startWith, switchMap } from 'rxjs';
import { SlowRequest, UsageReport } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';

/** Window choices, in UTC days ending today (the API takes 1 to 31). */
export const windows = [1, 7, 30] as const;

type State = { kind: 'loading' } | { kind: 'ready'; report: UsageReport } | { kind: 'error' };
type ClearState = 'idle' | 'busy' | 'done' | 'error';

const count = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });
const percent = new Intl.NumberFormat('en-US', { style: 'percent', minimumFractionDigits: 1, maximumFractionDigits: 1 });

/** A ratio as a percentage; null (nothing cacheable ran) shows as a dash, never NaN. */
export function ratioText(ratio: number | null): string {
  return ratio === null ? '—' : percent.format(ratio);
}

export function countText(n: number | null): string {
  return n === null ? '' : count.format(n);
}

/** The UTC time of day of an audit timestamp (the day is the row's group). */
export function timeText(at: string): string {
  return new Date(at).toISOString().slice(11, 19);
}

/**
 * Admin Usage (README §7.2, §8, #70): requests per user per day, the cache hit ratio and the slowest 20 requests of
 * each day, read live from the audit log. A new window or a refresh supersedes the request in flight (`switchMap`).
 * Admins also clear the server caches here (after a manual reseed).
 */
@Component({
  selector: 'app-usage',
  templateUrl: './usage.html',
  styleUrl: './usage.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UsagePage {
  private readonly api = inject(DeskApi);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly windows = windows;
  protected readonly days = signal<number>(7);
  private readonly reloads = signal(0);
  /** The day whose slowest requests are shown; null = the newest day in the report. */
  protected readonly slowDay = signal<string | null>(null);
  protected readonly clearState = signal<ClearState>('idle');

  protected readonly ratioText = ratioText;
  protected readonly countText = countText;
  protected readonly timeText = timeText;

  protected readonly state = toSignal(
    toObservable(computed(() => ({ days: this.days(), reload: this.reloads() }))).pipe(
      switchMap(({ days }) => this.api.usage(days).pipe(
        map((report): State => ({ kind: 'ready', report })),
        catchError(() => of<State>({ kind: 'error' })),
        startWith<State>({ kind: 'loading' }),
      )),
    ),
    { initialValue: { kind: 'loading' } as State },
  );

  protected readonly report = computed(() => {
    const s = this.state();
    return s.kind === 'ready' ? s.report : null;
  });

  /** Days that have slow requests, newest first (the API orders them so). */
  protected readonly slowDays = computed(() => [...new Set(this.report()?.slowest.map((s) => s.day) ?? [])]);

  protected readonly shownSlowDay = computed(() => {
    const days = this.slowDays();
    const wanted = this.slowDay();
    return wanted !== null && days.includes(wanted) ? wanted : (days[0] ?? null);
  });

  protected readonly slowest = computed<SlowRequest[]>(() => {
    const day = this.shownSlowDay();
    return this.report()?.slowest.filter((s) => s.day === day) ?? [];
  });

  protected setWindow(days: number): void {
    this.days.set(days);
  }

  protected refresh(): void {
    this.reloads.update((n) => n + 1);
  }

  protected pickSlowDay(event: Event): void {
    this.slowDay.set((event.target as HTMLSelectElement).value);
  }

  protected clearCache(): void {
    this.clearState.set('busy');
    this.api.clearCache().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.clearState.set('done'),
      error: () => this.clearState.set('error'),
    });
  }
}
