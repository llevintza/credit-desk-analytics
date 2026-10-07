# ADR-0016: CD: Actions-driven migrate → seed → Render deploy hook → smoke test

- **Status:** Accepted
- **Date:** 2026-10-07
- **Phase / PR:** phase-0/scaffold

## Context

Every merge to `main` should go live with the database schema and seed data in step, without anyone touching a dashboard. Free-tier constraints:
- Render's free plan has no pre-deploy hook and no shell.
- Neon autosuspends.
- Cold starts are slow, so they must stay cheap.

## Options considered

1. **Render auto-deploy + `start.sh` migrations and seed at boot** (the earlier projects' pattern).
2. **GitHub Actions drives it:** build the EF migrations bundle and seeder, migrate, seed only if the seed version changed, trigger the Render deploy hook for the exact commit, then smoke-test.
3. **Option 2 plus a Neon branch per PR.** Deferred: it costs a Neon branch per PR and adds little at this size.

## Evaluation

| Criterion | 1. Boot-time | 2. Actions-driven |
|---|---|---|
| A failed migration takes the site down | Yes (crash loop on boot) | **No.** The deploy stops; the old version keeps serving |
| DDL/seed on every cold start | Yes (a cheap check, but every wake) | **Never** |
| Exact commit deployed and verified | Not verified | **`/health` must report the merged SHA** |
| Seeding control | An env flag per deploy | **Seed version + scale in `app.seed_metadata`**; manual guarded reseed |
| Visibility | Render logs | **GitHub job summary** (migrations, seed action, DB size) |

**Rehearsed (local Postgres 17, then repeated in CI on every PR):**
- the bundle applied `InitialAppSchema`, and a second run was a no-op
- the seeder printed `SEED_ACTION=seeded`, then `SEED_ACTION=skipped`, then `seeded` again after a scale change
- `--max-mb 1` exited 2

## Decision

Option 2:
- `deploy.yml` runs after a green CI on `main`, in the `production` environment, under the non-cancelling `production` concurrency group.
- `db-ops.yml` provides guarded manual operations.
- Render `autoDeploy: false`.
- `start.sh` never migrates.

## Consequences

- **Migrations must be backward compatible** with the running version (expand → contract; README §14.4), because they run before the new app starts.
- **The seeder owns seed versioning.** Forgetting to bump `SeedVersion.Current` means production keeps old data. AGENTS.md calls this out.
- **One-time manual setup** (Neon project, Render blueprint, GitHub environment secrets; README §13.3). Until it's done, the preflight job skips the deploy with a summary instead of failing.
