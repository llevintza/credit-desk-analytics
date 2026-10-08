# Architecture Decision Records

Every technology or design choice is recorded here, **in the same PR that makes it**, with measured evidence where it applies. Copy `0000-template.md` to `NNNN-kebab-title.md` and add a row below.

| ADR | Title | Phase | Status |
|---|---|---|---|
| [0001](0001-stack.md) | Stack: .NET 10 + Postgres + Angular 22 | 0 Scaffold | Accepted |
| [0002](0002-hosting.md) | Hosting: single Render service + Neon (vs static site + API, vs GitHub Pages) | 0 Scaffold | Accepted |
| [0003](0003-wide-snapshot-narrow-history.md) | Wide position snapshot + narrow history | 1 Data | Accepted |
| [0004](0004-bulk-load-copy-vs-ef.md) | Bulk load: Npgsql binary COPY vs EF `AddRange` (measured) | 1 Data | Accepted |
| [0005](0005-auth-same-origin-cookie-vs-jwt.md) | Auth: same-origin cookie session vs JWT | 2 Auth and limits | Accepted |
| [0006](0006-dapper-vs-ef-for-the-dynamic-grid.md) | Dynamic grid reads: Dapper + columnar reader vs EF Core (measured) | 3 Positions API | Accepted |
| [0007](0007-row-json-vs-columnar-vs-messagepack.md) | Payload: columnar JSON by default, MessagePack on request (measured) | 3 Positions API | Accepted |
| [0008](0008-offset-vs-keyset-paging.md) | Paging: OFFSET + tie-breaker, sort indexes (measured) | 3 Positions API | Accepted |
| [0009](0009-infinite-row-model-vs-client-side.md) | Grid: Infinite Row Model + displayed-column requests vs client-side model (measured) | 4 Shell + Positions UI | Accepted |
| [0010](0010-signals-onpush-zoneless.md) | Angular: signals + OnPush + zoneless | 4 Shell + Positions UI | Accepted |
| 0011 | Fund performance: long format + edge pivot vs SQL pivot | 5 Fund Performance | Planned |
| 0012 | Insights: per-source endpoints + per-task DbContext vs 20 calls vs one call (measured) | 6 Insights Board | Planned |
| 0013 | "Any one bond": LATERAL vs ROW_NUMBER (EXPLAIN ANALYZE) | 7 Deal Explorer | Planned |
| 0014 | Free-tier guardrails (rate limits, cache-first, maintenance mode) | 9 Hardening | Planned |
| 0015 | Intraday overlay transport: SSE vs polling vs WebSockets | 10 Intraday (stretch) | Planned |
| [0016](0016-continuous-deployment.md) | CD: Actions-driven migrate → seed → Render deploy hook → smoke test | 0 Scaffold | Accepted |
| [0017](0017-deploy-path-safety.md) | Deploy-path safety: pipefail, main-only release, step-scoped DATABASE_URL | 0 Scaffold (follow-up) | Accepted |
| [0018](0018-coverage-gates-and-ci-hardening.md) | Coverage gates, action pinning, and CI hardening | 0 Scaffold (follow-up) | Accepted |
| [0019](0019-openapi-and-swagger-ui.md) | OpenAPI document + Swagger UI (off in production unless enabled) | cross-cutting (#93) | Accepted |
| [0022](0022-audit-retention-purge-on-write.md) | Audit retention: 90-day default (`AUDIT_RETENTION_DAYS`), purged after an audit write and on an idle timer (measured; timer added by #193) | 2 Auth and limits follow-up (#114) | Accepted |
| [0023](0023-per-area-ci-jobs.md) | CI runs only the jobs a change touches (base-sourced per-area classifier, measured) | [workflows] follow-up (#169) | Proposed |
