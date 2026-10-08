# ADR-0010: Angular with signals, OnPush and zoneless change detection

- **Status:** Accepted
- **Date:** 2026-10-08
- **Phase / PR:** phase-4/shell-and-positions-ui

## Context

The UI is dense and data-heavy:
- a grid of up to 200 columns;
- a status bar updated after every request;
- (in later phases) pages with many small grids that fill in progressively.

Re-rendering the whole tree on every async event (zone.js) is wasted work. It also makes "what changed" hard to reason about once streams, timers and the grid's own events mix. README §9.4 sets the rules: standalone components, OnPush everywhere, signals for state, RxJS for streams over time, no manual `subscribe` without `takeUntilDestroyed`.

## Options considered

1. **Zoneless + signals + OnPush.**
   - `provideZonelessChangeDetection()`.
   - Component state is signals, and views update only where a read signal changes.
   - RxJS stays for time-based streams (debounce, `switchMap`), bridged with `toSignal`.
2. **zone.js + OnPush + observables (async pipe).** Familiar, but zone.js patches every async API, which is extra bundle weight and every event schedules a check.
3. **zone.js + Default change detection.** The simplest, and the most wasteful for a grid-heavy app.

## Evaluation

| | Zoneless + signals (chosen) | zone.js + OnPush | Default |
|---|---|---|---|
| zone.js in the initial bundle | none | +~35 KB raw | +~35 KB raw |
| What triggers a view update | a signal the view reads | an input, an event or the async pipe | any async event, anywhere |
| AG Grid events (scroll, hover) | no app-wide check | zone check per event, unless run outside Angular | zone check per event |
| Testing timers | `vi.useFakeTimers()` works directly (the debounce test) | `fakeAsync` | `fakeAsync` |

**Measured outcome of the chosen setup:**
- The initial bundle is **91 KB Brotli**: `scripts/bundle-budget.mjs`, run as part of `npm run build`.
- The positions page paints its first rows in **167 ms** warm (ADR-0009).
- The 300 ms quick-filter debounce and the `switchMap` cancellation are unit-tested with plain Vitest fake timers (`positions-query.spec.ts`).

## Decision

- **Zoneless change detection, signals for state, OnPush on every component.**
  - Streams over time stay RxJS (`debounceTime`, `distinctUntilChanged`, `switchMap` in `PositionsQuery`; `retry` in `HealthService`).
  - They are bridged with `toSignal`, and every manual subscription (services and components) uses `takeUntilDestroyed`. The one unpiped `subscribe` is the inner one in `HealthService.state()`'s `new Observable` factory: it is not a manual subscription, its teardown unsubscribes it when the `toSignal` consumer goes away.
- **AG Grid is driven through its API** (`setGridOption`, `applyColumnState`, `purgeInfiniteCache`) from `effect()`s, never by re-binding large inputs. Bound data is never mutated: new arrays and objects are set.

## Consequences

- Nothing re-renders unless a signal it reads changes: the status bar updates without touching the grid, and vice versa.
- Third-party code that relies on zone.js to trigger change detection won't update the view. AG Grid doesn't need it (it renders itself), and our code sets signals explicitly.
- Effects must use `untracked` where they write to the grid or call into services, so they don't subscribe to more than they mean to (see `Positions`).
- `fakeAsync` isn't available without zone.js. Vitest fake timers replace it, and `whenStable` mustn't be awaited while timers are faked; the tests use `detectChanges` there.
