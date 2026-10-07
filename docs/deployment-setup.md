# Deployment setup: Neon + Render + GitHub (one time)

This is the step-by-step version of README §13.3. Do it once. After that, every merge to `main` deploys by itself:

> CI → migrate Neon → seed (only if the seed version changed) → Render deploy of the exact commit → smoke test.

**Time needed:** about 30 minutes. **Accounts:** Neon, Render, GitHub (repo admin), Anthropic Console.

> **Secrets rule:**
> - Connection strings, deploy hook URLs and API keys go **only** into the Neon, Render and GitHub dashboards.
> - Never paste them into the repo, a PR, an issue, a chat or a terminal command that gets logged.
> - Copy them straight from one dashboard to the other.

---

## Overview: what goes where

| Value | Created in | Stored in | Used by |
|---|---|---|---|
| Neon **direct** connection string | Neon (step 1) | Render env var `DATABASE_URL` **and** GitHub environment secret `NEON_DATABASE_URL` | The running app; the deploy pipeline's migrations and seeding |
| Render **Deploy Hook URL** | Render (step 2) | GitHub environment secret `RENDER_DEPLOY_HOOK_URL` | The deploy pipeline, to start a deploy |
| Render **service URL** | Render (step 2) | GitHub environment variable `APP_URL` | The deploy pipeline's smoke test |
| `SEED_SCALE` = `1.0` | n/a | GitHub environment variable | The seeder |
| Anthropic API key | Anthropic Console (step 4) | GitHub **repository** secret `ANTHROPIC_API_KEY` | The Claude review workflow on every PR |

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

Keep the Neon tab open: you'll paste the connection string in steps 2 and 3.

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

## Step 3: GitHub `production` environment (used by the deploy pipeline)

1. Open <https://github.com/llevintza/credit-desk-analytics> → **Settings** → **Environments** → **New environment**.
2. **Name:** `production`, exactly (the workflows reference it), then **Configure environment**.
3. **Recommended:**
   - **Deployment branches and tags:** choose **Selected branches and tags**, then add the rule `main`. Only `main` can deploy.
   - **Required reviewers:** optional. If you add yourself, every deploy waits for your click in the Actions tab.
4. **Environment secrets** → **Add environment secret**, once for each:

   | Name | Value |
   |---|---|
   | `NEON_DATABASE_URL` | the Neon **direct** connection string (same as Render's `DATABASE_URL`) |
   | `RENDER_DEPLOY_HOOK_URL` | the Render Deploy Hook URL from step 2.6 |

5. **Environment variables** → **Add environment variable**:

   | Name | Value |
   |---|---|
   | `APP_URL` | the service URL from step 2.5, **without** a trailing slash |
   | `SEED_SCALE` | `1.0` |

---

## Step 4: Anthropic API key (used by the Claude review on every PR)

1. Sign in at <https://console.anthropic.com> → **Settings → API Keys** → **Create Key**.
   - **Name:** `credit-desk-analytics PR review`.
   - Copy the key. It's shown once.
   - Under **Settings → Limits**, consider a monthly spend limit. Reviews are billed per token.
2. GitHub repo → **Settings → Secrets and variables → Actions** → **Repository secrets** → **New repository secret**:
   - **Name:** `ANTHROPIC_API_KEY`
   - **Value:** the key
3. This is a **repository** secret, not a `production` environment secret, because reviews run on PR branches.
4. No GitHub App installation is needed. The workflow passes its own `GITHUB_TOKEN` to the action, so review comments appear as **github-actions[bot]**. If you install the Claude GitHub App (<https://github.com/apps/claude>) instead and remove `github_token:` from the workflow, comments post as **claude[bot]**.

**What the review does:**
- It runs on every PR push (`.github/workflows/claude-review.yml`).
- It posts inline comments marked **[blocking]** or **[suggestion]**.
- It ends with a summary comment whose first line is `<!-- claude-review sha=<head> blocking=<n> -->`.

**The merge gate:**
- that summary exists for the head commit with `blocking=0`, **and is authored by `github-actions[bot]`** (anyone can type the marker in a comment)
- there are no unresolved review threads
- all checks are green

**One limit:** a PR can change `claude-review.yml` itself, and its own run would still post as `github-actions[bot]`. PRs that touch `.github/` are therefore titled with a **`[workflows]`** prefix, and their workflow diff is reviewed by hand before merge. Your merge is the human gate; you can't formally approve your own PRs on GitHub.

---

## Step 5: First deploy

1. GitHub → **Actions** → **Deploy** → **Run workflow** (branch `main`) → **Run workflow**.
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

From now on, merging a PR into `main` runs all of this automatically. When CI on `main` passes, **Deploy** starts by itself.

---

## Everyday operations

| Task | How |
|---|---|
| See what's deployed | `<APP_URL>/health` shows the commit SHA |
| Re-run a deploy | Actions → **Deploy** → Run workflow |
| Database size | Actions → **DB ops** → operation `size-report` |
| Apply migrations only | Actions → **DB ops** → `migrate` |
| Reload the synthetic data | Actions → **DB ops** → `reseed`, `scale` `1.0`, `confirm` `RESEED-PRODUCTION`. This never touches accounts. |
| Pause the site without touching the DB | Render → Environment → `MAINTENANCE_MODE=true` → Save (Render restarts the service). Effective once phase 2 lands. |
| Always-on for a demo window | Render → Settings → **Instance Type** → Starter (paid); switch back afterwards |

## Rotating credentials

- **Neon password:** Neon → **Roles** → `neondb_owner` → **Reset password**. Then update **both** Render `DATABASE_URL` and GitHub `NEON_DATABASE_URL`, then re-run **Deploy**.
- **Render deploy hook:** Render → Settings → Deploy Hook → **Regenerate**. Then update GitHub `RENDER_DEPLOY_HOOK_URL`.
- **Anthropic key:** create a new key, update `ANTHROPIC_API_KEY`, then revoke the old key.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Deploy summary: *"Deploy skipped: production environment not configured. Missing: …"* | A secret or variable from step 3 is missing or misnamed (names are case-sensitive; the environment must be called `production`) |
| Migrate step: `password authentication failed` | Wrong or rotated password, or the string was edited. Copy it again from Neon → Connect |
| Migrate step: errors mentioning prepared statements or `-pooler` | The **pooled** string was used. Copy it again with pooling **off** |
| Smoke test times out | Check Render → **Events / Logs**: a build failure, or the service failing to start (`DATABASE_URL is not set`) |
| `/health` shows an older SHA | The deploy is still building; free builds are slow. A failed build keeps the previous version running |
| Claude review job fails at "Require ANTHROPIC_API_KEY" | Add the repository secret from step 4 |
| Site takes 30–60 s to load the first time | The free instance spins down after about 15 min idle. Expected; the page shows "Waking the server…" |
