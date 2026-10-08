# Deployment setup: Neon + Render + GitHub (one time)

This is the step-by-step version of README §13.3. Do it once. After that, every merge to `main` deploys by itself:

> CI → migrate Neon → seed (only if the seed version changed) → Render deploy of the exact commit → smoke test.

> **Before step 4:** set the `production` environment to the `main` branch only, and set the main ruleset (required status checks only; no required approving review; no force-push or deletion). **No required reviewer** on `production`: merges to `main` auto-deploy. Controls are the pre-merge review gate, required checks (once the main ruleset is active), `deploy.yml` migrate/smoke, and README §14.4. Don't add production secrets until those protections are on. Don't add `APP_URL` (which turns deploys on) until Tech Coordinator gives the go-ahead.

**Time needed:** about 30 minutes. **Accounts:** Neon, Render, GitHub (repo admin), Anthropic Console.

> **Secrets rule:**
> - Connection strings, deploy hook URLs and API keys go **only** into the Neon, Render and GitHub dashboards.
> - Never paste them into the repo, a PR, an issue, a chat or a terminal command that gets logged.
> - Copy them straight from one dashboard to the other.
> - Production deploy/DB secrets live only in the GitHub `production` environment (never as repository secrets).
> - The Claude review key lives only in the GitHub `claude-review` environment (never `production`, never a repository secret).

---

## Overview: what goes where

| Value | Created in | Stored in | Used by |
|---|---|---|---|
| Neon **direct** connection string | Neon (step 1) | Render env var `DATABASE_URL` **and** GitHub `production` environment secret `NEON_DATABASE_URL` | The running app; the deploy pipeline's migrations and seeding |
| Render **Deploy Hook URL** | Render (step 2) | GitHub `production` environment secret `RENDER_DEPLOY_HOOK_URL` | The deploy pipeline, to start a deploy |
| Render **service URL** | Render (step 2) | GitHub `production` environment variable `APP_URL` | The deploy pipeline's smoke test |
| `SEED_SCALE` = `1.0` | n/a | GitHub `production` environment variable | The seeder |
| Anthropic API key | Anthropic Console (step 5) | GitHub **`claude-review` environment** secret `ANTHROPIC_API_KEY` (Leo's decision, 2026-10-07; never `production`, never a repository secret) | The Claude review workflow on same-repo PRs (forks and Dependabot skip) |

---

## Step 1: Neon (the database)

1. **Sign in** at <https://console.neon.tech> (GitHub sign-in is fine).
2. **Create the project:** **New Project** (or **Create project** on first use).
   - **Project name:** `credit-desk-analytics`
   - **Postgres version:** **17**
   - **Cloud provider / region:** **AWS · US East (N. Virginia)** (`aws-us-east-1`). This matches the Render region in `render.yaml` (`virginia`), so app ↔ DB latency stays at about 1–2 ms.
   - Click **Create**.

   Neon creates:
   - a default **branch** `main` (Neon branches are database copies, not git branches)
   - a database **`neondb`**
   - an owner role **`neondb_owner`**

   Use these defaults. The app creates its own schemas (`app`, and later `core`, `market`, …) inside `neondb`.
3. **Copy the direct connection string:**
   1. On the project dashboard, click **Connect** (top right).
   2. Choose **Branch:** `main`, **Database:** `neondb`, **Role:** `neondb_owner`.
   3. **Turn "Connection pooling" OFF.** You want the **direct** endpoint. The host must **not** contain `-pooler`.
   4. Pick the **Connection string** tab, then click **Copy snippet**.

   It looks like this (placeholders shown, not real values):
   ```
   postgresql://neondb_owner:<password>@ep-<words>-<id>.us-east-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require
   ```
   Use it as-is. The app accepts this URI form directly, maps `sslmode=require` to Npgsql's SSL mode, and ignores the libpq-only `channel_binding` parameter.

   **Why direct and not pooled:**
   - The API keeps its own connection pool (Npgsql).
   - EF Core migrations need a real session.
   - Neon's pooler (PgBouncer, transaction mode) is meant for serverless functions that open thousands of short connections.
4. **Optional checks:**
   - **Settings → Compute:** the free plan scales the compute to zero after 5 minutes idle. That's expected: the first query after idle takes about 0.5–1 s extra, and the app's cache-first design hides most of it.
   - **Storage limit** on the free plan is 0.5 GB per project. The seeder fails the deploy above 400 MB (README §5.4).

Keep the Neon tab open: you'll paste the connection string in steps 2 and 4.

---

## Step 2: Render (the web service)

1. **Sign in** at <https://dashboard.render.com>. Signing in with GitHub is easiest.
2. **Create from the Blueprint:**
   1. **New +** → **Blueprint**.
   2. **Connect GitHub** if prompted. In the GitHub app permission screen, choose **Only select repositories** → `llevintza/credit-desk-analytics` → **Install**. Render needs this to read the private repo.
   3. Select the repo `credit-desk-analytics`, branch **`main`**.
   4. **Blueprint Name:** `credit-desk-analytics`. Render reads `render.yaml` and shows **one web service, `credit-desk-analytics` (Docker, Free, Virginia)**.
3. **Fill in the env vars marked "sync: false"** on that page:
   - **`DATABASE_URL`:** paste the Neon **direct** connection string from step 1.
   - **`DEMO_ACCOUNTS_JSON`:** leave **empty**. Optional demo accounts arrive in phase 2.

   Then click **Apply** (or **Deploy Blueprint**).
4. **The first build starts automatically.** Render builds `deploy/Dockerfile`, which takes about 5–10 minutes on the free plan.
   - That's fine: the app doesn't touch the database at startup.
   - Later deploys are triggered **only** by GitHub Actions, because `autoDeploy` is off in `render.yaml`.
5. **Copy the service URL:** open the service (**Dashboard → credit-desk-analytics**). The URL is under the service name, e.g. `https://credit-desk-analytics.onrender.com`. If that name was taken, Render adds a suffix like `-abcd`.
6. **Copy the Deploy Hook URL:** in the service, go to **Settings → Deploy Hook**, then **Copy**. It looks like `https://api.render.com/deploy/srv-<id>?key=<secret>`. **The `key` makes this URL a secret:** anyone with it can trigger deploys.
7. **Optional checks:**
   - **Settings → Health Check Path** shows `/health`.
   - **Settings → Auto-Deploy** shows **No**.
   - **Environment** shows `DATABASE_URL` (masked) and `ASPNETCORE_ENVIRONMENT=Production`.
8. **Once the first build finishes,** open `<service URL>/health`. You should see `{"status":"ok","version":"<commit sha>"}`, and `<service URL>/` shows the placeholder page.

---

## Step 3: GitHub protections (do this before any production secret)

Workflow YAML cannot set these. Do them by hand.

1. Open <https://github.com/llevintza/credit-desk-analytics> → **Settings** → **Environments**.
2. **Create `production`** if it does not exist (**New environment**, name `production` exactly), then **Configure environment**.
3. **Restrict it now:**
   - **Deployment branches and tags:** choose **Selected branches and tags**, then add the rule `main`. Only `main` can deploy.
   - **Required reviewers:** leave unset. No required reviewer exists; merges to `main` auto-deploy. Controls are the pre-merge review gate, required status checks (once the main ruleset is active), `deploy.yml` migrate/smoke, and README §14.4.
4. **Ruleset on `main`** (Settings → Rules → New ruleset, target `main`):
   - **Required status checks:** every CI job except `review` (`secrets`, `api`, `web`, `coverage`, `compose-smoke`, `workflows`, `db-tools`, `gate-tests`). Do **not** require `review` (Claude review is advisory; a skipped draft would count as passing).
   - **No required approving review.** Every bot acts as `llevintza` and cannot self-approve, so a required PR review would deadlock every merge.
   - Block force-pushes and deletions of `main`.

Confirm `NEON_DATABASE_URL` and `RENDER_DEPLOY_HOOK_URL` will be **environment** secrets on `production`, not repository secrets.

---

## Step 4: Production secrets (only after step 3)

1. Environment **`production`** → **Environment secrets** → **Add environment secret**, once for each:

   | Name | Value |
   |---|---|
   | `NEON_DATABASE_URL` | the Neon **direct** connection string (same as Render's `DATABASE_URL`) |
   | `RENDER_DEPLOY_HOOK_URL` | the Render Deploy Hook URL from step 2.6 |

2. **Environment variables** → **Add environment variable**:

   | Name | Value |
   |---|---|
   | `SEED_SCALE` | `1.0` |

3. **Don't add `APP_URL` yet.** That variable is what lets the deploy workflow run its release job. Wait for Tech Coordinator's go-ahead, then set it to the service URL from step 2.5, **without** a trailing slash.

---

## Step 5: Anthropic API key (Claude review; Leo's decision, 2026-10-07)

The review job is **advisory and not a required check**. Leo's decision (2026-10-07): a dedicated `claude-review` GitHub environment holding a spend-capped key. Not `production` (that environment is main-only; PR jobs must never see it). Not a repository secret. Same-repo PRs use this key. `cursor[bot]` (agent pushes) is allowed via `allowed_bots`; forks, Dependabot and other bots skip. Each run creates a GitHub deployment on the PR (no reviewers, no branch restriction, so those jobs can read the key).

1. Sign in at <https://console.anthropic.com> → **Settings → API Keys** → **Create Key**.
   - **Name:** `credit-desk-analytics PR review`.
   - Use a **dedicated** key (or workspace) that nothing else uses.
   - Copy the key. It's shown once.
   - Under **Settings → Limits**, **Set** a monthly spend limit (**required**). Reviews are billed per token.
2. If `ANTHROPIC_API_KEY` already exists as a **repository** secret, delete it after step 3 of this list succeeds (rotate if there is any suspicion it was exposed).
3. GitHub repo → **Settings** → **Environments** → **New environment**:
   - **Name:** `claude-review` exactly (the workflow references it).
   - **No** deployment-branch restriction (PR branches must be able to use it).
   - **No** required reviewers.
   - **Environment secrets** → **Add environment secret**:
     - **Name:** `ANTHROPIC_API_KEY`
     - **Value:** the key
4. It is unknown whether the Claude GitHub App (<https://github.com/apps/claude>) is installed on this repo. The workflow passes its own `GITHUB_TOKEN`, so no App install is required and review comments appear as **github-actions[bot]**. If you later install the App and remove `github_token:` from the workflow, comments post as **claude[bot]** and the job would need `id-token: write`.

**What the review does:**
- It runs on every same-repo, non-draft PR against `main` (`.github/workflows/claude-review.yml`).
- It posts inline comments marked **[blocking]** or **[suggestion]**.
- It ends with a summary comment whose first line is `<!-- claude-review sha=<head> blocking=<n> -->`.
- If the key is missing, the job **skips with a notice** and stays green. `cursor[bot]` (agent pushes) is allowed via `allowed_bots`; forks, Dependabot and other bots skip. Keep `review` **out** of the required checks on `main`.

**The Claude review is advisory.** Its `blocking=<n>` is the model's own count, and any workflow running as `github-actions[bot]` can post the marker, so it never decides a merge.

The review gate (Tech Coordinator plus Code Reviewer; Claude's review is advisory only):
1. Every suite (API xUnit, web Vitest, compose smoke) passes in CI on the PR head, with nothing skipped, disabled or weakened.
2. coverlet and Vitest coverage are collected and published in CI, with the numbers in the PR summary; ≥80% on new or changed code; main never drops. Missing coverage means REQUEST CHANGES.
3. Any workflow, action, Dockerfile, render.yaml, `perf/coverage-*`, `tests/testconfig.json`, or `.gitleaks.toml` change gets governance review: SHA-pinned actions, least-privilege permissions, secrets only in the `production` environment (sole exception: the capped Claude key in `claude-review`), no unsafe `pull_request_target`, gitleaks stays on, nothing removed or loosened.

Tech Coordinator merges and starts the next phase.

Per-area CI ([ADR-0023](adr/0023-per-area-ci-jobs.md)): a heavy job skipped because the base-sourced `changes` classifier reported its flag as exactly `false` was not affected by the diff, and is not "skipped" under clause 1. Any other skip (a failed or cancelled dependency, a missing classifier output, a disabled step) is. Code Reviewer checks the `changes` job summary. Clause 1 still applies in full to every job that runs.

**Known limit:** every bot acts as `llevintza`, so GitHub can't require an approving review and CODEOWNERS is advisory only. The `[workflows]` title prefix is also advisory only: no protection enforces it. The control is process: only Tech Coordinator (or Leo) merges. Same-repo PRs can edit `claude-review.yml` and use the `claude-review` key; accepted because the review is advisory and the key is dedicated and spend-capped. Forks and Dependabot skip. `cursor[bot]` (agent pushes) is allowed via `allowed_bots`; other bots skip.

---

## Step 6: First deploy

**Prerequisite:** Tech Coordinator's go-ahead, then step 4.3 (`APP_URL`). There is no production approval pause; Deploy starts when CI on `main` is green.

1. GitHub → **Actions** → **Deploy** → **Run workflow** (branch **main**) → **Run workflow**.
2. Open the run. The **preflight** job should report all values present. Then **release** runs:
   - **Build migrations bundle and seeder:** about 2 min.
   - **Migrate Neon:** prints `Applying migration '…_InitialAppSchema'`, then `Done.`
   - **Seed:** prints `SEED_ACTION=seeded …` the first time, and `skipped` afterwards. Then `DB_SIZE_MB=…`.
   - **Trigger Render deploy:** prints `Deploy requested for <sha>`.
   - **Smoke test:** polls `/health` until `version` equals the commit. This takes 5–15 min on the free plan.
3. The run **Summary** shows the URL, the migrations and the seed result.
4. **Check the database in Neon:** **SQL Editor** → database `neondb` →
   ```sql
   SELECT version, seed, scale, completed_at, database_size_bytes FROM app.seed_metadata ORDER BY id;
   ```

From now on, merging a PR into `main` runs all of this automatically. When CI on `main` passes, **Deploy** starts by itself (no production approval pause).

---

## Everyday operations

| Task | How |
|---|---|
| See what's deployed | `<APP_URL>/health` shows the commit SHA |
| Re-run a deploy | Actions → **Deploy** → Run workflow (branch **main**) |
| Database size | Actions → **DB ops** → operation `size-report` |
| Apply migrations only | Actions → **DB ops** → `migrate` |
| Reload the synthetic data | Actions → **DB ops** → `reseed`, `scale` `1.0`, `confirm` `RESEED-PRODUCTION`. This never touches accounts. |
| Pause the site without touching the DB | Render → Environment → `MAINTENANCE_MODE=true` → Save (Render restarts the service). Effective once phase 2 lands. |
| Always-on for a demo window | Render → Settings → **Instance Type** → Starter (paid); switch back afterwards |

## Rotating credentials

- **Neon password:** Neon → **Roles** → `neondb_owner` → **Reset password**. Then update **both** Render `DATABASE_URL` and GitHub `NEON_DATABASE_URL`, then re-run **Deploy**.
- **Render deploy hook:** Render → Settings → Deploy Hook → **Regenerate**. Then update GitHub `RENDER_DEPLOY_HOOK_URL`.
- **Anthropic key:** create a new spend-capped key, update the `claude-review` environment secret `ANTHROPIC_API_KEY`, then revoke the old key. Remove any leftover repository secret of the same name.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Deploy summary: *"Deploy skipped: production environment not configured. Missing: …"* | A secret or variable from step 4 is missing or misnamed (names are case-sensitive; the environment must be called `production`). `APP_URL` is omitted on purpose until Tech Coordinator says go |
| Migrate step: `password authentication failed` | Wrong or rotated password, or the string was edited. Copy it again from Neon → Connect |
| Migrate step: errors mentioning prepared statements or `-pooler` | The **pooled** string was used. Copy it again with pooling **off** |
| Smoke test times out | Check Render → **Events / Logs**: a build failure, or the service failing to start (`DATABASE_URL is not set`) |
| `/health` shows an older SHA | The deploy is still building; free builds are slow. A failed build keeps the previous version running |
| Claude review job skipped with a missing-key notice | Add `ANTHROPIC_API_KEY` to the `claude-review` environment (step 5). The job stays green; it is not a required check |
| Claude review job waits on a deployment approval | The `claude-review` environment must have **no** required reviewers and **no** branch restriction |
| Site takes 30–60 s to load the first time | The free instance spins down after about 15 min idle. Expected; the page shows "Waking the server…" |
| `NETSDK1004` / assets file not found while bundling | `dotnet tool restore` does not write `project.assets.json`. The composite action restores `Desk.Data` and `Desk.Seeder` for `linux-x64` on a clean checkout (CI `db-tools`, deploy, db-ops) |
| `No connection string named 'App'` / `No connection string for source App` | `AppDbContextDesignFactory` needs `DATABASE_URL` or `ConnectionStrings__App` at bundle time. The composite sets a password-less design-time `DATABASE_URL` only for that step; production `DATABASE_URL` stays on migrate/seed |
