# Angular 22 and AG Grid Community

Linked from [`web/AGENTS.md`](../../../web/AGENTS.md). Absorbs the former `ag-grid-infinite` skill. Root Angular and AG Grid rules still apply.

## Stack

- Angular ^22.2 (signals / OnPush / zoneless, ADR-0010), TypeScript ~6.0, Vitest ^5 + jsdom, angular-eslint 22.5, prettier.

## Layout

- `web/src/app/{core,data-access,login,positions,shell}`, with each `*.spec.ts` beside its source.
- API client in `data-access/desk-api.ts` and `api.types.ts`.

## AG Grid Community 36.2.0 only

- Register just the modules used, in `positions/grid-setup.ts` (`gridModules`; `ValidationModule` in dev only).
- Infinite Row Model: requests carry the displayed columns; stable row ids; `switchMap` for supersedable queries.
- Theme `deskGridTheme` (CSS variables, 24 px rows).
- Do not import or enable Enterprise modules (root rule).

## Bundle

- `npm run build` = `ng build && node scripts/bundle-budget.mjs`: initial JS < 500 KB Brotli.
- The grid page is a lazy chunk (reported, not budgeted); keep it lazy.

## Commands

```
cd web && npm run lint && npm test -- --watch=false --coverage
```

Writes `web/coverage`, which feeds the gate.
