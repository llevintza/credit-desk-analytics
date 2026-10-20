# Architecture Decision Records

Every technology or design choice is recorded here, **in the same PR that makes it**, with measured evidence where it applies. Copy `0000-template.md` to `NNNN-kebab-title.md` and add a row below.

| ADR | Title | Phase | Status |
|---|---|---|---|
| [0001](0001-stack.md) | Stack: .NET 10 + Postgres + Angular 22 | 0 Scaffold | Accepted |
| [0002](0002-hosting.md) | Hosting: single Render service + Neon (vs static site + API, vs GitHub Pages) | 0 Scaffold | Accepted; partially superseded by 0024 |
| [0003](0003-wide-snapshot-narrow-history.md) | Wide position snapshot + narrow history | 1 Data | Accepted |
| [0004](0004-bulk-load-copy-vs-ef.md) | Bulk load: Npgsql binary COPY vs EF `AddRange` (measured) | 1 Data | Accepted |
| [0005](0005-auth-same-origin-cookie-vs-jwt.md) | Auth: same-origin cookie session vs JWT | 2 Auth and limits | Superseded by 0024; addendum 2026-10-07 (#115, `viewer`/`admin` roles seeded by the AuthAndAudit migration, and the conditions for any seeded reference rows) |
| [0006](0006-dapper-vs-ef-for-the-dynamic-grid.md) | Dynamic grid reads: Dapper + columnar reader vs EF Core (measured) | 3 Positions API | Accepted |
| [0007](0007-row-json-vs-columnar-vs-messagepack.md) | Payload: columnar JSON by default, MessagePack on request (measured) | 3 Positions API | Accepted |
| [0008](0008-offset-vs-keyset-paging.md) | Paging: OFFSET + tie-breaker, sort indexes (measured) | 3 Positions API | Accepted; addendum 2026-10-16 (#326, `random_page_cost` 1.1 for the app role) |
| [0009](0009-infinite-row-model-vs-client-side.md) | Grid: Infinite Row Model + displayed-column requests vs client-side model (measured) | 4 Shell + Positions UI | Accepted |
| [0010](0010-signals-onpush-zoneless.md) | Angular: signals + OnPush + zoneless | 4 Shell + Positions UI | Accepted |
| [0011](0011-long-format-edge-pivot.md) | P2: long format + edge pivot vs SQL pivot (measured) | 5 Fund Performance | Accepted |
| [0012](0012-insights-per-source-endpoints.md) | Insights: per-source endpoints + per-task connections vs 20 calls vs one call; plain-table tiles (measured) | 6 Insights Board | Accepted |
| [0013](0013-lateral-vs-row-number.md) | "Any one bond per deal": LATERAL … LIMIT 1 vs ROW_NUMBER() = 1 vs DISTINCT ON (EXPLAIN ANALYZE, measured) | 7 Deal Explorer | Accepted |
| [0014](0014-free-tier-guardrails.md) | Free-tier guardrails: per-user burst 60, response cache 64 MB (capped at 96), preset lists cached; repeat views read no database (measured on a 512 MiB stack) | 9 Hardening (#67) | Proposed |
| [0015](0015-intraday-overlay-transport-sse.md) | Intraday overlay transport: SSE read with `fetch` (bearer header) vs polling vs WebSockets; bounded stream lifetime, idle pause (measured) | 10 Intraday (stretch) | Proposed |
| [0016](0016-continuous-deployment.md) | CD: Actions-driven migrate → seed → Render deploy hook → smoke test | 0 Scaffold | Accepted |
| [0017](0017-deploy-path-safety.md) | Deploy-path safety: pipefail, main-only release, step-scoped DATABASE_URL | 0 Scaffold (follow-up) | Accepted |
| [0018](0018-coverage-gates-and-ci-hardening.md) | Coverage gates, action pinning, and CI hardening | 0 Scaffold (follow-up) | Accepted |
| [0019](0019-openapi-and-swagger-ui.md) | OpenAPI document + Swagger UI (off in production unless enabled) | cross-cutting (#93) | Accepted |
| [0020](0020-harness-neutral-agent-setup.md) | Harness-neutral agent setup: one `AGENTS.md` source, generated Cursor adapters, base-sourced drift checks, a local guard hook (guard p99 48 ms, measured) | cross-cutting (#171) | Accepted, rev 14 (Helms 2026-10-16; built in #171) |
| [0021](0021-per-user-portfolio-entitlements.md) | Per-user portfolio entitlements (model B: per-user grants) | cross-cutting (#122) | Accepted; implemented in #122 (mode defaults to `all`); amended 2026-10-16 (#70, `/api/admin/usage` exempt) |
| [0022](0022-audit-retention-purge-on-write.md) | Audit retention: 90-day default (`AUDIT_RETENTION_DAYS`), purged after an audit write and on an idle timer (measured; timer added by #193) | 2 Auth and limits follow-up (#114) | Accepted |
| [0023](0023-per-area-ci-jobs.md) | CI runs only the jobs a change touches (base-sourced per-area classifier, measured) | [workflows] follow-up (#169) | Proposed |
| [0024](0024-github-pages-frontend-bearer-auth.md) | GitHub Pages front end + Identity bearer tokens, CORS to the Render API (vs custom-domain cookie, vs `SameSite=None` cookie; measured) | cross-cutting (#349) | Accepted (Leo, 2026-10-11); amended 2026-10-15 (#353), 2026-10-16 (#354, proposed), 2026-10-16 (#66: frame guard, meta referrer, one-year HSTS; proposed) |
| [0025](0025-ci-image-pulls-without-docker-hub-limits.md) | CI image pulls without the Docker Hub anonymous limit: same digests from ghcr.io, plus a mirror.gcr.io daemon mirror with Docker Hub fallback (vs a Docker Hub token, vs an image cache; digests verified) | [workflows] follow-up (#332) | Proposed |
| [0026](0026-protected-ref-ci-wiring-check.md) | Protected-ref check for the CI classifier wiring tests: base-sourced `pull_request_target` job + ruleset required check (vs org ruleset required workflow, unavailable on a personal-account repo; vs CODEOWNERS; vs the #441 checklist; measured) | cross-cutting docs (#451) | Proposed (evaluation only; Leo makes any ruleset change) |
| [0027](0027-admin-usage-queries.md) | Admin Usage reads: one GROUP BY plus a LATERAL top-N per day on `IX_audit_at`, live and uncached (vs ROW_NUMBER, vs one statement, vs a new index; measured) | 9 Hardening (#70) | Proposed |
| [0028](0028-performance-lab-switches-and-measurement.md) | Performance Lab: header switches on the real endpoints (cache off for admins, forced encoding in dev), row JSON on request, MessagePack compressed, measured with Resource Timing (measured) | 8 Performance Lab | Proposed |
| [0029](0029-demo-access-requests.md) | Demo access: an anonymous request queue (202 always, 3/hour per IP, honeypot and 3 s fill time, 200 pending) approved by Leo in an admin view; password shown once, 14-day `viewer`, cap 25, 30-day retention (vs self sign-up; bounds worked out) | cross-cutting (#534, #537) | Accepted (Leo, 2026-10-20) |
