// Runs with `node --test` from `npm test` (#209).
import { describe, it } from 'node:test';
import { RuleTester } from 'eslint';
import tseslint from 'typescript-eslint';
import rule from './no-unmanaged-subscribe.js';

RuleTester.describe = describe;
RuleTester.it = it;
RuleTester.itOnly = it.only;

const ruleTester = new RuleTester({ languageOptions: { parser: tseslint.parser } });
const unmanaged = [{ messageId: 'unmanaged' }];
const notLast = [{ messageId: 'notLast' }];

ruleTester.run('no-unmanaged-subscribe', rule, {
  valid: [
    'api.load().pipe(takeUntilDestroyed(this.destroyRef)).subscribe();',
    'api.load().pipe(map((x) => x), takeUntilDestroyed(this.destroyRef)).subscribe({ next: () => undefined });',
    // Multi-line pipe, as in positions-query.ts and scope.service.ts.
    `this.view$.pipe(
      switchMap((v) => this.blocks$.pipe(mergeMap((r) => this.fetch(r)))),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe();`,
    `forkJoin({ a: api.a(), b: api.b() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: () => undefined });`,
    'api.load().pipe(takeUntilDestroyed()).subscribe(); // injection context, no DestroyRef',
    'const s = toSignal(api.load());',
    'poll.unsubscribe();',
    'this.loadSub?.unsubscribe();',
    'subscribe(x);',
    'obj[name](x);',
  ],
  invalid: [
    { code: 'api.load().subscribe();', errors: unmanaged },
    { code: 'api.load().subscribe({ next: (x) => use(x) });', errors: unmanaged },
    { code: 'subject$.subscribe(subscriber);', errors: unmanaged },
    { code: 'api.load().pipe(map((x) => x)).subscribe();', errors: unmanaged },
    { code: 'api.load().pipe().subscribe();', errors: unmanaged },
    { code: 'api.load()?.subscribe();', errors: unmanaged },
    { code: "api.load()['subscribe']();", errors: unmanaged },
    // takeUntilDestroyed on an inner observable, or under another name, does not manage the outer subscription.
    { code: 'outer$.pipe(switchMap(() => inner$.pipe(takeUntilDestroyed(d)))).subscribe();', errors: unmanaged },
    { code: 'api.load().pipe(takeUntil(this.destroy$)).subscribe();', errors: unmanaged },
    { code: 'api.load().pipe(rx.takeUntilDestroyed(d)).subscribe();', errors: unmanaged },
    {
      code: `this.typed$.pipe(
        debounceTime(300),
        distinctUntilChanged(),
      ).subscribe((q) => this.update({ q }));`,
      errors: unmanaged,
    },
    { code: 'api.load().pipe(takeUntilDestroyed(d), switchMap((x) => other(x))).subscribe();', errors: notLast },
  ],
});
