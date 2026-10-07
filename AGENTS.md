# AGENTS.md: working rules for this repo

Read `README.md` first. It's the spec, and its MUST items are acceptance criteria. These rules govern **how** the work is done.

## Workflow: the history is part of the deliverable

1. **One phase = one branch = one pull request.** Phases and branch names come from README §15 (`phase-N/<slug>`).
2. **Never push to `main`.** `main` changes only by merging a reviewed PR.
3. **Small, single-purpose commits** in Conventional Commits style (`feat(api): …`, `test(web): …`, `docs(adr): …`, `perf(api): …`). Each commit builds and passes tests.
4. **Each technology choice gets an ADR in the same PR** (`docs/adr/NNNN-kebab-title.md`, copied from `0000-template.md`, then the index in `docs/adr/README.md` updated):
   - Each one has: context, options considered, **evaluation with measured numbers** where the choice affects performance, decision, consequences.
   - Measurements must be reproducible: commit the script under `perf/` and paste the command and output.
5. **The PR body follows `.github/pull_request_template.md`:**
   - summary
   - the decisions and ADR links
   - before/after measurements
   - Playwright screenshots for UI changes (dark + light)
   - the test list
   - coverage numbers (coverlet and Vitest: line/branch overall and on new/changed code, link to the CI summary)
   - an updated README §17 Status row
6. **Every PR also gets an automated Claude review** (README §14.5). Claude's PR review is ADVISORY only. Merges to main and kickoff of the next §15 phase happen only through the review gate (Tech Coordinator plus Code Reviewer).

   The review gate (Tech Coordinator plus Code Reviewer; Claude's review is advisory only):
   1. Every suite (API xUnit, web Vitest, compose smoke) passes in CI on the PR head, with nothing skipped, disabled or weakened.
   2. coverlet and Vitest coverage are collected and published in CI, with the numbers in the PR summary; ≥80% on new or changed code; main never drops. Missing coverage means REQUEST CHANGES.
   3. Any workflow, action, Dockerfile, render.yaml, `perf/coverage-*`, or `tests/testconfig.json` change gets governance review: SHA-pinned actions, least-privilege permissions, secrets only in the `production` environment (sole exception: the capped Claude key in `claude-review`), no unsafe `pull_request_target`, gitleaks stays on, nothing removed or loosened.

   Tech Coordinator merges and starts the next phase.

   - Fix [blocking] comments on the same branch and push. Don't resolve a reviewer thread you haven't fixed.
   - **Tech Coordinator merges. Never merge a PR yourself, including your own.** A Claude `blocking=0` marker, self-resolved threads, or green checks without coverage are not the gate and do not authorize a merge or the next phase.
   - **After a merge, wait for Tech Coordinator to start the next phase.** Don't start it yourself.
   - Changes to `.github/`, `.claude/`, `CLAUDE.md`, `AGENTS.md`, README §14, `perf/coverage-*`, or `tests/testconfig.json` go in their own `[workflows]` PR, reviewed by hand by Tech Coordinator and Leo. CODEOWNERS and the `[workflows]` title prefix are advisory only: no GitHub protection enforces them (every bot acts as the owner and cannot approve its own PR).
   - **The PR is closed without merging:** stop and ask.
7. **Stop after opening each PR** and wait for review. Don't start the next phase on top of an unmerged one. After Tech Coordinator merges, wait for Tech Coordinator to start the next phase, then branch from fresh `origin/main` (squash by default; a merge commit is also fine).

## Hard rules

- **No secrets in git, ever:**
  - `.env.example` holds key names only.
  - Connection strings, cookie keys and demo-account JSON live in the Render/Neon dashboards or a local `.env`.
  - Production deploy/DB secrets live only in the GitHub `production` environment (never as repository secrets).
  - The Claude review API key is Leo's decision (2026-10-07): a dedicated `claude-review` environment (not `production`, not a repository secret), holding a spend-capped key used only by `.github/workflows/claude-review.yml`.
  - The UserAdmin CLI prints generated passwords **once** and never logs them.
  - Never paste credentials into commits, PRs, issues or ADRs.
  - **No default or example credentials either**, not even for local throwaway databases: no `Password=…` literals in compose, CI, code or docs. Local passwords are generated into the gitignored `.env`. CI databases use trust auth or a per-run random password.
  - The CI `secrets` job (gitleaks, `.gitleaks.toml`) scans the **full history** of every PR. A finding means rewrite the branch (fixup + autosquash) before merge, not a follow-up commit.
- **No real company, employer, client or person names** in code, data, docs, commits or screenshots. All data is synthetic and fictional.
- **SQL safety:** client-supplied identifiers (columns, sort, filter keys) are resolved through the column-catalog whitelist. Every value is a parameter. Never concatenate client input into SQL.
- **EF Core:**
  - Never share a DbContext across concurrent operations; use `IDbContextFactory` per parallel task.
  - Use `AsNoTracking` + projections for reads.
  - No captive dependencies (a scoped context inside a singleton).
- **Async:** pass the `CancellationToken` everywhere. No `.Result`, `.Wait()` or `async void`.
- **Money:** use `decimal` / `numeric`. Round only at the display edge. Empty or zero weights return `null`, never `NaN`.
- **Angular:**
  - OnPush + signals.
  - `switchMap` for supersedable queries.
  - `takeUntilDestroyed` for any manual subscription.
  - No mutation of bound data.
  - `@for` tracks a stable id.
- **AG Grid Community only.** Don't import or enable Enterprise modules.
- **Respect the free tiers:**
  - no keep-awake pingers
  - health probes never touch the DB
  - cache-first reads
  - rate limits stay on in every environment except unit tests

## Commands (fill these in during phase 0 and keep them current)

| Task | Command |
|---|---|
| Start Postgres | `docker compose up -d postgres` |
| Restore tools (dotnet-ef) | `dotnet tool restore` |
| Apply migrations (local) | `dotnet ef database update --project src/Desk.Data --startup-project src/Desk.Data` |
| Add a migration | `dotnet ef migrations add <Name> --project src/Desk.Data --startup-project src/Desk.Data --output-dir App/Migrations` |
| Seed (local) | `dotnet run --project src/Desk.Seeder -- --if-changed --scale 1.0` (`--force` to reseed, `--size-report`) |
| API | `dotnet run --project src/Desk.Api` (http://localhost:5180) |
| API tests | `dotnet test` (coverlet.MTP, not `--collect "XPlat Code Coverage"`) |
| Coverlet (local) | `dotnet test -- --coverlet --coverlet-output-format cobertura --coverlet-include '[Desk.*]*' --coverlet-exclude-by-file '**/obj/**' --coverlet-exclude-by-file '**/*.generated.cs' --coverlet-exclude-assemblies-without-sources MissingAll` (GeneratedCodeAttribute exclusions live in `tests/testconfig.json`; do **not** add `CompilerGeneratedAttribute`, which strips `Program.cs` lambdas) |
| Coverage gates | `node perf/coverage-gate.mjs --dotnet TestResults/coverage --web web/coverage --base origin/main` |
| Gate-script tests | `node --test --experimental-test-coverage --test-coverage-lines=80 --test-coverage-branches=80 --test-coverage-include=perf/coverage-gate.mjs perf/coverage-gate.test.mjs` |
| Web dev server | `cd web && npm start` (http://localhost:4200, proxies to :5180) |
| Web lint + unit tests | `cd web && npm run lint && npm test -- --watch=false` |
| Whole stack | `docker compose up --build` (http://localhost:8080) |
| Lint workflows | `docker run --rm -v "$PWD":/repo -w /repo rhysd/actionlint:latest` |
| Create a user (phase 2) | `dotnet run --project src/Desk.UserAdmin -- add --email … --role viewer --expires YYYY-MM-DD` |
| E2E (phase 4) | `cd e2e && npx playwright test` |
| Payload budget (phase 3) | `node perf/payload-size.mjs` |

## Deployment

- **Merging to `main` deploys** (README §14.2): CI → migrate Neon → seed if the seed version changed → Render deploy hook → smoke test on `/health` version.
- **Bump `SeedVersion`** in the seeder whenever the generator or seeded schema changes. Otherwise production keeps the old data.
- **Migrations must be backward compatible** with the running app (expand → contract, README §14.4).
- **Never run DDL or seeding from the app at startup.**

## Definition of done for any PR

- CI is green: build, tests, lint, e2e, budgets (each once it exists).
- The review gate (Tech Coordinator plus Code Reviewer; Claude's review is advisory only):
  1. Every suite (API xUnit, web Vitest, compose smoke) passes in CI on the PR head, with nothing skipped, disabled or weakened.
  2. coverlet and Vitest coverage are collected and published in CI, with the numbers in the PR summary; ≥80% on new or changed code; main never drops. Missing coverage means REQUEST CHANGES.
  3. Any workflow, action, Dockerfile, render.yaml, `perf/coverage-*`, or `tests/testconfig.json` change gets governance review: SHA-pinned actions, least-privilege permissions, secrets only in the `production` environment (sole exception: the capped Claude key in `claude-review`), no unsafe `pull_request_target`, gitleaks stays on, nothing removed or loosened.
- Acceptance criteria for the touched pages are checked off in the PR body.
- ADRs are written for the choices made, with numbers.
- README §17 Status is updated. Nothing secret is in the diff.
- Tech Coordinator merges and starts the next phase. Don't start the next §15 phase until Tech Coordinator does.
