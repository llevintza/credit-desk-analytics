# web: Angular client

Area file for `web/` (Angular workspace and Vitest unit tests). The root `AGENTS.md` still applies in full. Spec: README §9 (engineering rules: §9.4) and §10.

## Angular rules

- Standalone components, `ChangeDetectionStrategy.OnPush` everywhere, signals for state (README §9.4).
- `switchMap` for any query that can be superseded.
- `takeUntilDestroyed` for any manual subscription; prefer `toSignal` or the async pipe.
- Never mutate arrays or objects bound to the view; set new references.
- `@for` always tracks a stable id.

## AG Grid

- **Community only.** Don't import or enable Enterprise modules.
- Use the `ag-grid-infinite` skill for grid work.

## Commands

| Task | Command |
|---|---|
| Web dev server | `cd web && npm start` (http://localhost:4200, proxies to :5180) |
| Web lint + unit tests | `cd web && npm run lint && npm test -- --watch=false` |

## PRs that touch the UI

- Attach Playwright screenshots in dark and light themes (PR template).
- Vitest coverage counts toward gate clause 2 (≥80% on new or changed code; main never drops).
- Keep the initial JS bundle under the README §10 budget.

## Health

- Don't probe the dev server from the shell (no curl or wget, no exception). Check the `npm start` output for a clean compile; lint, unit tests and CI cover the rest ([`workflow.md`](../docs/agents/workflow.md#health-checks)).
