# ADR-0021: Per-user portfolio entitlements (model B: per-user grants)

- **Status:** Accepted (Helms, 2026-10-07). Leo can override any ruling below.
- **Date:** 2026-10-07
- **Phase / PR:** cross-cutting. Decides #43's entitlements criterion, which has moved to #122 (implementation). AGENTS.md rule: #123. This ADR's commit: #124.
- **Target path:** `docs/adr/0021-per-user-portfolio-entitlements.md`, plus a row in `docs/adr/README.md` (AGENTS.md rule 4).
  - The same docs PR also adds a reserved index row for 0020 ("Harness-neutral agent setup, Proposed, not yet committed") so 0021 doesn't follow an unexplained gap.
- **Lands ahead of the implementation (Helms, 2026-10-07):** under Leo's hold, this ADR is committed on its own docs-only PR (#124) before #122 implements it. This is a deliberate one-off exception to AGENTS.md rule 4 and the index convention that an ADR lands in the same PR as the change; #122 must cite ADR-0021.
- **How the number was chosen:**
  - main `docs/adr/` has 0000–0005 and 0016–0019. 0019 is #93's, already merged.
  - The index reserves 0009–0015 for planned ADRs.
  - #121 is the only open PR (head `7e820eb8`) and adds 0006–0008.
  - 0020 is reserved for the harness-neutral agents ADR, which is not yet committed.
  - The lowest free number is therefore **0021**.
- **Verified at:** #121 head `7e820eb8243dd294e46653bb5ceb4720c99aed3d` and main `c91c4b51d7a1f856eb4cdfe0891c35c03e4aaf3c`, via the cursor-github connector.
  - Paths without a SHA are at the #121 head.
  - Files #121 doesn't touch are identical at both SHAs.

## Context

- **Requirement.** #43 says "Results restricted to the user's portfolio entitlements". This criterion now lives in #122. #44 and README §6 key the cache on the entitlements (README.md:338). #47 makes `/api/meta/portfolios` the entitlements endpoint.
- **What #121 ships:**
  - `IPortfolioEntitlements.For(ClaimsPrincipal, MetaSnapshot)` is sync and returns `IReadOnlyCollection<int>` (src/Desk.Api/Positions/PortfolioEntitlements.cs:10-13).
  - Its only implementation, `AllPortfolios`, returns every portfolio (:15-18). It is registered as a singleton (src/Desk.Api/Program.cs:32).
- **Phase 2 auth:**
  - Roles: `viewer` and `admin` are Identity roles with fixed ids (src/Desk.Data/Auth/Roles.cs:6-7, 13-14), seeded into `auth.roles` and joined through `auth.user_roles` (migration `20261008014846_AuthAndAudit`).
  - Role check: the only one is `AdminPolicy` (src/Desk.Api/Auth/AuthSetup.cs:25, 61-62).
  - Claims: `DeskClaimsFactory` builds them at login and on each security-stamp refresh (src/Desk.Data/Auth/DeskClaimsFactory.cs:8-25), every 5 min (AuthSetup.cs:31).
- **Constraints:**
  - `core` may move to its own DB (20261007195929_DataSchemas.cs:10-11; #74).
  - The seeder `TRUNCATE`s `core.portfolio` without CASCADE (src/Desk.Seeder/Loader.cs:14-22).
  - Migrations must be additive (README.md:858-864).
  - Scale: about 60 users (README.md:35) and 12 portfolios (README.md:191).

## The single scoping point (#121, unchanged by this ADR)

1. `PositionsEndpoints.Resolve` calls `entitlements.For(...)` for both `/query` and `/export` (src/Desk.Api/Positions/PositionsEndpoints.cs:47, 105, 153-159). It runs **before** the 304 check (:60).
2. `GridQueryNormalizer.Normalize` intersects the requested ids with the entitled set (src/Desk.Data/Grid/GridQueryNormalizer.cs:40-41).
3. `GridSqlBuilder.Where` always emits `portfolio_id = ANY(@portfolios)` (src/Desk.Data/Grid/GridSqlBuilder.cs:90). The page, summary and export all share it (:45, :55, :72).
4. `PortfolioIds` is part of every cache key and ETag (src/Desk.Data/Grid/GridQuery.cs:26, 34 → PositionsEndpoints.cs:52-54, 67, 77).

**Paths that bypass the SQL scoping point:**
- `/api/meta/portfolios` filters inside its endpoint (src/Desk.Api/Positions/MetaEndpoints.cs:72-74).
- `/api/meta/as-of` is unscoped (src/Desk.Data/Grid/MetaRepository.cs:37-43) and returns dates only. **Ruling: exempt** from entitlement scoping, because it returns snapshot dates and no portfolio-owned data. It loses the exemption, and must resolve through `IPortfolioEntitlements`, if it ever returns per-portfolio dates or ids.
- The unscoped portfolio list is loaded into the singleton `MetaSnapshot` (MetaRepository.cs:45-55 → src/Desk.Api/Positions/MetaCache.cs:12, 41-42).
- `GridRepository` accepts any `GridQuery` (src/Desk.Data/Grid/GridRepository.cs:16, 41). Today only PositionsEndpoints.cs:79 and :127 call it.
- Future insights and deals queries (README.md:406-412, 446) will sit outside it too.

Exempt, because they return no portfolio-owned data (the same test as R4): `/api/meta/columns` returns column metadata, and the presets endpoints return only the caller's own presets, scoped by user (MetaEndpoints.cs:60-66, 78-111). Any portfolio ids saved inside a preset are re-intersected through `IPortfolioEntitlements` when the preset runs a query, so a revoked grant never returns rows through an old preset.

## Options considered

1. **A. Roles only.** Keep `AllPortfolios`. No schema change, but it fails #43's criterion.
2. **B. Per-user grants table** `app.portfolio_grant`, resolved into claims. The chain above stays as it is.
3. **C. Group grants.** Groups or funds map to portfolios: 3 tables (1 for the fund variant). Same chain as B, but more admin work for one desk.

Rejected enforcement alternatives:
- **RLS or a view on `core`.** It needs grants from `app` visible in `core`, which breaks when `core` moves DB (#74), plus a per-request `SET` on pooled connections.
- **Per-endpoint checks.**

## Evaluation

| Criterion | A | B | C |
|---|---|---|---|
| Meets #43 / #122 criterion | No | Yes | Yes |
| #121 code changed | None | DI line + new class | DI line + new class |
| New tables | 0 | 1 | 3 (or 1) |
| Enforcement | existing chain | existing chain | existing chain |
| Admin effort at about 60 users / 12 portfolios | none | low (CLI) | medium |

## Decision

**Model B.** A per-user `app.portfolio_grant` table, with **no FK to `core`**, behind `ENTITLEMENTS_MODE=all|grants`. **The default is `all`**, which is today's behaviour, until grants exist.

```csharp
migrationBuilder.CreateTable(name: "portfolio_grant", schema: "app",
    columns: t => new {
        user_id      = t.Column<Guid>(type: "uuid", nullable: false),
        portfolio_id = t.Column<int>(type: "integer", nullable: false),   // no FK: core is TRUNCATEd / may move DB
        granted_at   = t.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
        granted_by   = t.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true) },
    constraints: t => {
        t.PrimaryKey("PK_portfolio_grant", x => new { x.user_id, x.portfolio_id });
        t.ForeignKey("FK_portfolio_grant_users_user_id", x => x.user_id, principalSchema: "auth",
            principalTable: "users", principalColumn: "id", onDelete: ReferentialAction.Cascade); });
migrationBuilder.CreateIndex("IX_portfolio_grant_portfolio_id", schema: "app", table: "portfolio_grant", column: "portfolio_id");
```

**Where it plugs in:**
- Add one additive migration after #121's `20261008024514_SnapshotSortIndexes`. Neither #121 migration and no `core` index changes.
- Add a `PortfolioGrant` entity to `AppDbContext`, following #121's `Preset` block.
- `DeskClaimsFactory.GenerateClaimsAsync` (DeskClaimsFactory.cs:20-25) adds one `desk:portfolio` claim per grant.
- A new `GrantedPortfolios : IPortfolioEntitlements` goes beside `AllPortfolios` (PortfolioEntitlements.cs:15-18).
- Program.cs:32 picks the implementation from `ENTITLEMENTS_MODE`. An unknown value fails startup.
- `Desk.UserAdmin` gets `grant`/`revoke` commands and shows grants in `list`. They must **not** rotate the security stamp, because that signs the user out.

**Rollout order:**
1. Merge #121.
2. Merge and deploy #122 with the mode still `all`. The migration runs before the app (README §14.2), and nothing changes for users.
3. Load viewer grants with the CLI and set the demo-portfolio config (E2).
4. Verify with `list` and a single-portfolio test account.
5. Set `ENTITLEMENTS_MODE=grants` in the Render environment.

**Rollback:** set the mode back to `all`. No migration is needed.

## Rulings (Helms, 2026-10-07; Leo can override)

- **E1: admins see all portfolios.** `GrantedPortfolios` returns every `meta.UnscopedPortfolios` id when `user.IsInRole(Roles.Admin)`. Admins need no grants.
- **E2: zero grants means 0 portfolios (fail closed), and demo accounts get the demo portfolios.**
  - The empty set flows to `ANY('{}')`, which returns 0 rows.
  - Grants are intersected with `meta.UnscopedPortfolios`, so ids lost in a reseed drop out.
  - The repo doesn't define "the demo portfolios": `DemoAccount` has only email, password, role and expires (src/Desk.Api/Auth/DemoAccounts.cs:95-99).
  - Mechanism: add an optional `portfolios` array to each `DEMO_ACCOUNTS_JSON` entry. `FindOrCreateAsync` writes those grants after `AddToRoleAsync` (DemoAccounts.cs:51). An entry without it gets zero grants. Leo or TC set the actual ids in the secret.
- **E3: grants are per portfolio.** Model C (groups or funds) comes later only if the desk needs it. It would expand into the same `desk:portfolio` claims without touching enforcement.
- **E4: unentitled requests are silently intersected.** They return the granted subset or 0 rows, never 403. This is the existing behaviour (GridQueryNormalizer.cs:40-41; GridQueryTests.cs:42-47).
- **E5: up to 5 minutes of revocation lag, with no forced logout, is acceptable for now.** See RESIDUAL under Consequences.
- **E6: #43's entitlements criterion is split out.** **#122 is the split-out issue.** Its body says it "now carries #43's entitlements acceptance criterion", and #43's comment of 2026-10-08 03:16 UTC says the criterion "has moved to #122".
- **E7: AGENTS.md rule (#123).** Add this exact text under "Hard rules", after "SQL safety":
  > - **Portfolio entitlements (ADR-0021):** every endpoint that returns portfolio-owned data (positions, trades, insights, deals, fund roll-ups, and any query outside `GridSqlBuilder`) resolves its portfolio scope through `IPortfolioEntitlements` and applies it in SQL (`portfolio_id = ANY(@portfolios)` or the equivalent key). Never read the unscoped portfolio list (`MetaSnapshot.UnscopedPortfolios`, `MetaRepository.PortfoliosAsync`) outside the allowlist in `EntitlementArchitectureTests`. Each such endpoint ships with an integration test using a stub provider: a single-portfolio stub sees only that portfolio, and an empty set returns 0 rows. `IPortfolioEntitlements.For` stays synchronous and does no I/O. The known bypass paths are listed in ADR-0021.

## Additional requirements

- **R1. Unscoped-cache check.** Add a test `EntitlementArchitectureTests` in tests/Desk.Api.Tests, part of #122:
  1. Rename `MetaSnapshot.Portfolios` (MetaCache.cs:12) to `UnscopedPortfolios` so the name can be grepped exactly.
  2. The test scans `src/**/*.cs` and fails on any file that references `UnscopedPortfolios` or `PortfoliosAsync(`, or that injects `MetaRepository`, unless the file is allowlisted:
     - `PortfolioEntitlements.cs`: the provider implementations.
     - `MetaEndpoints.cs`: it filters at :72-74.
     - `MetaCache.cs`: the loader at :41-42.
     - `MetaRepository.cs`: the definition.
     - `Program.cs`: DI registration only.
  3. The failure message cites ADR-0021.
  4. A behavioural test pairs with it: a stub provider returning {p1} makes `/meta/portfolios` return exactly [p1].
- **R2. The provider stays sync with no I/O.** Put this comment on `IPortfolioEntitlements`:
  ```csharp
  /// <remarks>MUST stay synchronous and do no I/O (ADR-0021): it runs on every positions request, including
  /// cache HITs and before the 304 check. Read entitlements from the principal's claims (loaded at sign-in and
  /// refreshed by the security-stamp validator) or from <see cref="MetaSnapshot"/>; never query a database here.</remarks>
  ```
- **R3. Suggestions for #121 (for CR and TC, not actions):**
  1. Remove #43 from #121's "Closes" list. The PR body at `7e820eb8` still lists it.
  2. Add one stub-provider test that registers {p1} and covers `/query`, `/export` and `/meta/portfolios`, plus an empty set → 0 rows and a header-only CSV. If it doesn't land in #121, #122 must carry it.
- **R4. Tests for #122:**
  - Grants mode, for X={p1}, Y={p2}, an admin, a user with no grants, and a demo account:
    - `/query`: rows are p1 only. `rowCount` and `summary` equal independent SQL.
    - Partial intersection works.
    - Cache isolation: Y gets `X-Cache: MISS` and a different ETag; X's ETag never yields a 304 for Y.
  - `/export` and `/meta/portfolios` give the same results.
  - The `all` mode is unchanged.
  - An unknown mode fails startup.
  - Revocation: advance the fake `TimeProvider` past 5 min and access is gone.
  - Run `EXPLAIN` for a single-portfolio user on the sort indexes (SnapshotSortIndexes.cs:14-20).
  - Extend the OpenAPI path-list test (tests/Desk.Api.Tests/HardeningTests.cs:118-129) so every `/api` path must be classified as scoped or exempt. The exempt list is `/api/meta/as-of` (dates only, see above) plus endpoints that return no portfolio-owned data at all (#122 enumerates them from the OpenAPI path list, each with a one-line reason); adding a path after #122 merges needs an ADR amendment.

## #121 fit verdict

#121's query layout takes B **without rewrites**. #43 does not need to block #121's merge, because #121 already pushes a per-request entitlement set through one injected provider into a mandatory SQL predicate and into every cache key and ETag. B is therefore a DI swap plus one additive migration.

## Consequences

- **Easier:** data scoping lives in one place. Users with identical sets share cache entries, and grants reuse the CLI pattern.
- **Harder:** the scoping chain is a convention. It is enforced by #123's rule, R1 and the OpenAPI guard. Per-set cache keys lower the hit rate.
- **RESIDUAL (E5):** a revoked grant or role keeps working until the next security-stamp refresh, up to 5 min (AuthSetup.cs:31), and nothing forces a logout. This is accepted for now. Revisit if a real-time revoke is required, for example by rotating the stamp to force a sign-out.
- **Revisit:** when SSO arrives (README §16) with IdP groups, which favours C, or when `core` moves to its own DB.

## Issue alignment (read 2026-10-08 ~03:16 UTC; gaps only, for TC)

- **#122 (implementation; the E6 split-out).** It matches the decision on model B, no FK to `core`, the `all` default, E1, E2, E4, and sync with no I/O. Gaps:
  1. The R1 unscoped-cache architecture test.
  2. The stub-provider tests (R3.2) and the OpenAPI scoped/exempt guard.
  3. The rollout order and rollback, and the unknown-mode-fails-startup rule.
  4. CLI `grant`/`revoke` without stamp rotation.
  5. The demo-portfolio mechanism (E2). "Demo portfolios" isn't defined anywhere.
  6. Intersecting with `meta.UnscopedPortfolios` to handle reseed drift.
  7. The E5 revocation test.
  8. The `EXPLAIN` check for narrow sets.
  9. The R2 comment text.
- **#123 (AGENTS.md).** It matches the stub-test rule. Gaps:
  1. It doesn't name "any query outside `GridSqlBuilder`".
  2. It lacks the unscoped-list allowlist and the sync/no-I/O clause.
  3. It doesn't cite the ADR number.
  Use the E7 text above.
- **#124 (commit this ADR).** Its content list matches. Gaps:
  1. No number or path. It should be `docs/adr/0021-per-user-portfolio-entitlements.md`.
  2. The `docs/adr/README.md` index row is missing.
