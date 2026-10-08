# Agent governance: `agents-governance.yml` (planned; arrives with the harness build PR), guards, bootstrap

> Draft companion to ADR-0020 (Revision 13; points from revs 10–13 are marked **rev 10**, **rev 11**, **rev 12**, **rev 13**). Repo path: `docs/agents/governance.md`. Tech Writer revision (2026-10-07, 10 PM ET) of the Architect's draft, which it supersedes; changes are marked **[TW]** and listed in the drafts README.

## Helms's sign-off (H1), recorded for THESE TWO JOBS ONLY

> Helms (CTO), 2026-10-07. This is the gate-3 sign-off for "no *unsafe* `pull_request_target`". It covers `agents-drift (base)` and `governance-paths (base)` in `.github/workflows/agents-governance.yml` (planned; arrives with the harness build PR) **only**, with `permissions: contents: read, pull-requests: read` and **no secrets**. Any other `_target` use needs its own review; D20 fails on it.

- **Rules (Helms):**
  - **Never check out PR head code.** No `actions/checkout` of the head ref or SHA; base checkout only.
  - **No secrets.**
  - **Permissions:** `contents: read` and `pull-requests: read` only.
  - **Data only:** the jobs read only the changed-file list and PR blobs via the API, and never execute them. The base checker runs against fetched PR file contents written to a scratch dir. Nothing runs `npm install` or scripts from PR content.
- **Reason:** on plain `pull_request`, the PR's own workflow file runs, so a PR could loosen its own rules.
- **Push-to-main runs** (no PR) use a normal checkout of `main` in `agents.yml` (`agents-generate`). That is trusted code; no `_target` is involved.

## Workflow (both jobs written out in full; R3-N1, R3-F2)

```yaml
name: agents-governance
# Helms (CTO) governance sign-off, 2026-10-07: pull_request_target for these TWO jobs only.
# Data-only. No secrets. Never checks out the PR. Any other _target use needs its own review (D20).
on:
  pull_request_target:
    types: [opened, synchronize, reopened, edited, ready_for_review]
    branches: [main]
permissions: {}
concurrency:
  group: agents-governance-${{ github.event.pull_request.number }}
  cancel-in-progress: true
jobs:
  agents-drift-base:                       # no job-level if: (a skipped required check counts as passing)
    name: agents-drift (base)
    runs-on: ubuntu-latest
    timeout-minutes: 5
    permissions:
      contents: read
      pull-requests: read
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1; default _target ref = main tip, never the PR
        with:
          persist-credentials: false
      - uses: actions/setup-node@820762786026740c76f36085b0efc47a31fe5020 # v7.0.0
        with:
          node-version: 22.12.0
          package-manager-cache: false     # v5+ auto-caches when package.json declares packageManager (R3-F2)
      - name: Fetch PR steering files as data (API only)
        env:
          GH_TOKEN: ${{ github.token }}    # contents: read + pull-requests: read; not a secret reference
        run: node scripts/agents/pr-data.mjs --out "$RUNNER_TEMP/pr"
      - name: Base generator + base checker over PR data
        run: node scripts/agents/check-drift.mjs --root "$RUNNER_TEMP/pr/tree" --paths "$RUNNER_TEMP/pr/paths.json" --base . --regen
  governance-paths-base:                   # no job-level if:
    name: governance-paths (base)
    runs-on: ubuntu-latest
    timeout-minutes: 5
    permissions:
      contents: read
      pull-requests: read
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1; default _target ref = main tip, never the PR
        with:
          persist-credentials: false
      - uses: actions/setup-node@820762786026740c76f36085b0efc47a31fe5020 # v7.0.0
        with:
          node-version: 22.12.0
          package-manager-cache: false
      - name: Governance paths (API file list as data)
        env:
          GH_TOKEN: ${{ github.token }}
        run: node scripts/agents/governance.mjs
```

- **No PR strings reach `run:` or `env:`.** Both scripts read the PR number and SHAs from `$GITHUB_EVENT_PATH` (guard 6, "Better" form).
- **Concurrency (R3-N1):** the group is per PR with `cancel-in-progress`, so a title `edited` event cancels an in-flight `agents-drift (base)`. That is fine: the new run reports for the same head.
- `agents.yml` setup-node steps also set `package-manager-cache: false`, for consistency.

## `pr-data.mjs` (head content as data; guards 5, 7, 10, 11)

1. **Start:** read the event JSON, then `GET /pulls/{n}`.
   - Fail if `head.sha` ≠ the event's `head.sha`, or if `changed_files ≥ 3000`.
2. **Changed files:** `GET /pulls/{n}/files?per_page=100`, all pages.
   - Assert the count equals `changed_files`.
   - Keep `filename` and `previous_filename`.
   - Accept only the documented `status` values; anything else fails.
   - Never read `patch`.
3. **Tree:** `GET /git/trees/{head_sha}?recursive=1`, asserting `truncated: false`.
   - Write `paths.json` with path, mode, type, size **and blob `sha`** for every entry (R3-N2).
   - **Path validation:** reject empty, `.` or `..` segments, a leading `/`, backslashes and control characters. The job fails closed.
4. **Modes:**
   - Mode `120000` (symlink) at a steering or enforcement path fails (D15). Elsewhere it is recorded and never written.
   - Mode `160000` (submodule) fails if it is new, changed or at a steering path.
5. **Blobs:** `GET /git/blobs/{sha}` only for the **input set** declared in **main's** `adapters.json`:
   - `**/AGENTS.md`, `**/CLAUDE.md`, `.claude/**`, `.cursor/**`, `.cursorignore`, `docs/agents/**`, `scripts/agents/*.json`, `scripts/agents/hooks/**`;
   - `README.md`, `docs/deployment-setup.md`, `.gitleaks.toml`, `.github/workflows/*.y*ml`, `.mcp.json`, `.gitignore`.
   - Not yet `.github/actions/**`: it must be added to main's `adapters.json` before D21 can widen to composite actions (R4-F7).
   - **More than 500 matching blobs → fail** (no truncation; R3-N2).
   - **Each blob:** ≤ 1 MB (checked against the tree size and again after decoding). Fail on NUL bytes or invalid UTF-8 (`TextDecoder` with `fatal`).
   - Deleted files are absent from the head tree, so they are compared against base, never fetched.
6. **Writing:** blobs go to `$RUNNER_TEMP/pr/tree/<path>` with `writeFileSync(…, {flag: 'wx', mode: 0o644})`.
   - The resolved path must stay under the scratch root, and nothing creates symlinks.
   - The files are **data**: read with `readFileSync`, parsed only by `JSON.parse` or line-anchored markers, never `import`ed, `require`d, run or `npm install`ed.
   - **Output list and paths come from main's `adapters.json`** (R3-N2). The PR's `adapters.json` is data compared against it, never a source of write targets.
7. **End:** `GET /pulls/{n}` again; fail if `head.sha` moved (the next `synchronize` re-runs).
8. **Output:**
   - Names are printed only `JSON.stringify`-encoded inside `::stop-commands::<random token>`.
   - The step summary is written only by base code, HTML-escaped and capped (guards 7–8).

## `governance-paths (base)` (required once DEP-R1/DEP-R2; H1)

- **Code:** main's `governance.mjs`. It reads the PR number from `$GITHUB_EVENT_PATH`, and the title from the same event JSON; the title is never printed. The file list comes from the shared `pr-data.mjs` functions (guard 10): the 3,000 cap, the count assertion and the status whitelist.
- **Matching:** both `filename` and `previous_filename`, so a rename out of `.github/` still matches. Deletions are included. Case-insensitive.
- **Path list:**
  - `.github/**`, `**/Dockerfile*`, `**/*.Dockerfile`, `render.yaml`, `deploy/**`, `docker-compose*.y*ml`, `compose*.y*ml`
  - `.claude/**`, `**/.claude/**`, `.cursor/**`, `.cursorignore`, `.grok/**`, `.mcp.json`
  - `**/AGENTS.md`, `**/AGENT.md`, `**/CLAUDE.md`, `**/CLAUDE.local.md`, `.claudeignore`
  - `docs/agents/**`, `scripts/agents/**`, `.gitleaks.toml`, `perf/coverage-*`, `tests/testconfig.json`, `.gitmodules` (`.gitleaks.toml`, `perf/coverage-*`, `tests/testconfig.json` as in main's AGENTS `[workflows]` bullet since #5)
  - **Not on the list (rev 11, H19):** `src/Desk.UserAdmin/**` and other feature or auth code. governance-paths covers the rules that judge PRs; feature PRs such as #122 must not be forced under `[workflows]` titles (H9). The UserAdmin area file is covered by `**/AGENTS.md`/`**/CLAUDE.md`, and the auth directories get advisory CODEOWNERS lines (below).
- **On a match:**
  - The step summary (escaped and capped, guard 8) lists the files as "governance review required (gate 3 + Leo)".
  - The job **fails unless the title starts with `[workflows]`**. The `edited` trigger re-runs it after a title fix.
- **[TW] Raise-only coverage exemption (PR #107 ruling (a); C3/OQ-5; rev 10). Confirmed by Helms, 2026-10-07.** The exemption covers **only line and branch values in `perf/coverage-baseline.json` that go up or stay the same, with the same keys.** Main's `governance.mjs` leaves that one file out of the match set only when all of these hold; otherwise it matches the file like any other governance path:
  - The file is modified (not added, deleted or renamed), and both its base and head blobs parse as JSON objects. Anything unparseable, a missing base or an API read error fails closed (no exemption).
  - Head has the same keys as base, at every level (today `dotnet.line`, `dotnet.branch`, `web.line`, `web.branch`).
  - Every line and branch value is a number that goes up or stays the same against base, and no field other than `line`/`branch` changes. The floor comes from base, never head.
  - **Any change in the same PR to `perf/coverage-thresholds.json`, `perf/coverage-override.json` (the ADR-0018 measurement-scope override name #108 restores), `tests/testconfig.json`, any other file that sets coverage measurement or thresholds, or another governance path voids the exemption.** That voider set is an explicit allowlist in `governance.mjs` (ADR §7); anything ambiguous fails closed. The PR then needs its own `[workflows]` review; a lowering or a measurement-scope change also needs an override record and sign-off from Code Reviewer, Tech Coordinator and Helms.
  - Code Reviewer's governance check still applies (PR #107).
  - **Fixtures (`testing.md`; `r10-governance.txt`, 9/9 on the prototype), each must fail the exemption:** a lowered value; an added key; a deleted key; an unparseable file; a change to a non-line/branch field; a raise-only bump plus a `perf/coverage-thresholds.json` change; a raise-only bump plus a `perf/coverage-override.json` change; **a raise-only bump plus a `tests/testconfig.json` change**. Same keys with values that go up or stay the same, and no other coverage or governance file changed, pass.
  - **Data path:** main's `adapters.json` blob input set gains `perf/coverage-baseline.json`, so `pr-data.mjs` fetches its head blob as data; the base copy comes from the base checkout.
- **NOT a security boundary.** The author controls the title. The job is a base-run **signal** that the PR can't remove or shrink. **Human governance review remains the control (Helms decision).**

## CR2 R2-F1 guard checklist: adopted verbatim; all MUST

Item 14's names are the check names used throughout. Item 17 is applied in the ADR's Alternatives. Where item 6 offers a choice, the design uses its "Better" form, so no PR string appears in `run:` or `env:`. R3-F2/R3-F3 add assertions to D20 (see `drift-checks.md`).

1. **Trigger:** `on: pull_request_target: types: [opened, synchronize, reopened, edited, ready_for_review]`, `branches: [main]`. No `labeled`, `unlabeled`, `assigned`, `review_requested`, `workflow_dispatch` or `issue_comment`.
2. **Permissions:** top-level `permissions: {}`; each job `contents: read`, `pull-requests: read`, nothing else. The `GITHUB_TOKEN` has no write scope, and D20 asserts the exact block.
3. **No secrets:** no `secrets.*` reference, no `environment:`, no `id-token`.
4. **No PR code:**
   - no `actions/checkout` of `github.event.pull_request.head.*`, `github.head_ref`, `refs/pull/*` or `merge_commit_sha`;
   - if a checkout is needed for the base scripts, it uses the default `_target` ref (base tip) or `ref: ${{ github.event.pull_request.base.sha }}`, with `persist-credentials: false`;
   - no `git fetch` of PR refs.
5. **Head content is data only.**
   - Read it only via the REST API: `GET /pulls/{n}`, `GET /pulls/{n}/files` (paginated), `GET /git/trees/{head_sha}?recursive=1`, `GET /git/blobs/{sha}`.
   - Never `node`/`bash`/`source`/`require`/`import`/`eval` a head blob.
   - Parse only with `JSON.parse` or the line-anchored markers (N2).
6. **Untrusted strings never reach `run:` through `${{ }}`.**
   - Title, branch names, file names and paths go in via `env:` (`TITLE: ${{ github.event.pull_request.title }}`) and are used only as `"$TITLE"`, quoted.
   - Only numeric and SHA fields (`number`, `base.sha`, `head.sha`) may appear in `env:`.
   - Better: do all processing inside one base-owned Node script that reads the event JSON (`$GITHUB_EVENT_PATH`) and the API itself. No shell loops over file names; NUL-safe throughout.
7. **Log and workflow-command injection:**
   - Never print raw names.
   - Emit names `JSON.stringify`-encoded, which escapes newlines and CRs, so no output line can start with `::`.
   - Additionally wrap untrusted output in `::stop-commands::<random 32-hex token>` … `::<token>::`.
   - No `::error file=<untrusted>`-style annotations with raw paths.
8. **Step-summary injection:**
   - Write `$GITHUB_STEP_SUMMARY` only from the base script.
   - HTML-escape `& < > " '`, backticks, `|`, `[` and `]` in every name; render names in a table or code span.
   - Cap the list at 200 entries and each name at 300 characters, with a "+N more" line.
   - Never echo the PR title or body.
9. **No cross-job or privileged channels:**
   - no writes to `GITHUB_ENV`, `GITHUB_PATH` or `GITHUB_OUTPUT` derived from PR data;
   - no `actions/cache` or `setup-node` `cache:`;
   - no `upload-artifact`;
   - no `workflow_run` consumers of these jobs.
10. **API completeness, fail closed:**
    - `GET /pulls/{n}` first. Fail if `changed_files ≥ 3000`.
    - Paginate `/files` with `per_page=100` and **assert the collected count == `changed_files`**.
    - Take `filename` **and** `previous_filename`. Accept `status` ∈ {added, modified, removed, renamed, copied, changed, unchanged}; any other value fails.
    - Re-fetch `head.sha` at the end and fail if it changed (TOCTOU; the next `synchronize` re-runs).
    - Never use the files API `patch` field, which is truncated or absent for large or binary diffs.
11. **Tree and blob reads, fail closed:**
    - The tree response must have `truncated: false`.
    - Any mode `120000` (symlink) entry under a steering or enforcement path fails (D15).
    - Any mode `160000` (submodule) entry anywhere that is new, changed or at a steering path fails (a `.gitmodules` change already trips `governance-paths`).
    - Blob size cap 1 MB for steering files; above it, fail.
    - Decode base64 and fail on NUL bytes (binary) or invalid UTF-8 where text is expected.
    - Deleted files are checked against base, not fetched from head.
12. **Pinned actions only** (checkout `3d3c42e5…`, setup-node `82076278…`), or none (runner `node` + `gh api`).
13. **`concurrency: { group: agents-governance-${{ github.event.pull_request.number }}, cancel-in-progress: true }`** and `timeout-minutes: 5` per job.
14. **No skippable required jobs, no spoofing.**
    - No job-level `if:`, because a skipped required check counts as passing. Fork PRs run too; the job is data-only.
    - Give the jobs unique required-check names (e.g. `agents-drift (base)`, `governance-paths (base)`).
    - Base D20 fails if any **head** workflow defines a job or `name:` equal to a required base-run check name.
    - Spike (U18) that `_target` check runs report on the PR head as required statuses before relying on them. This was round-1 F4's open spike.
15. **D20 (base-run, new):**
    - `pull_request_target` appears **only** in `agents-governance.yml` (planned; arrives with the harness build PR). Any other workflow using it fails, because Helms's sign-off covers these two jobs only.
    - That file satisfies items 1–4, 9, 12 and 13 by line-anchored checks: exact trigger types, exact `permissions`, no `secrets.`, no `environment:`, no `head.sha`/`head.ref`/`head_ref`/`refs/pull` in any `uses:` or `with:`, no `actions/cache`/`upload-artifact`, no `GITHUB_ENV`/`GITHUB_PATH`, no `${{ github.event.pull_request.(title|body|head.ref|head.label) }}` or `github.head_ref` inside a `run:` block.
16. **Document in the ADR:** this is Helms's governance sign-off for gate-3 "no *unsafe* `pull_request_target`", scoped to these two jobs. Any other `_target` use needs its own review.
17. **Alternatives (l.450):** replace the row with "plain `pull_request` for governance-paths: rejected (Helms): the PR's own YAML could remove it."

## Spoofing the required checks (R3-F3, U18; residual risk accepted by Helms, R4-F4)

- D20 (base) closes the cheap routes. It fails if:
  - any workflow other than `agents-governance.yml` (planned; arrives with the harness build PR) has a job-level `name:` containing `${{`, or a job id or job-level name containing `agents-drift` or `governance-paths` (case-insensitive, NFKC-normalized, zero-width characters stripped; comments ignored, so the D12 markers don't match; R4-N4);
  - any workflow grants `statuses: write`, `checks: write` or `write-all`;
  - any workflow lacks a top-level `permissions:` block.
- Checked on `main` @ `96df50c2`, `6570f304` (CR4) and `982d00d3` (CR5); re-checked at `741b19eb` (rev 8; #5 changed `ci.yml`, `deploy.yml`, `db-ops.yml` and added `gitleaks.yml`) and `27c1c35a` (rev 9; #104 changed only `ci.yml`'s `coverage` job steps); unchanged at the rev 10 baseline `c91c4b51`; re-checked at the rev 11 baseline `9b83f7ef` (#125 added `ci.yml`'s `budgets` job: job-level `permissions: contents: read`, SHA-pinned actions, no secrets): all five workflows (`ci`, `claude-review`, `deploy`, `db-ops`, `gitleaks`) have a top-level `permissions: contents: read`, none grants `statuses`/`checks` write, and none uses `pull_request_target`.
- **Default workflow token (DEP-R3): VERIFIED 2026-10-07** (TC's read-only check, 6:06 PM ET: "Read repository contents and packages"; "Allow GitHub Actions to create and approve pull requests" off). This is **hygiene, not a spoof control**: a job name needs no permission, and a head workflow's explicit `permissions:` overrides the default for same-repo PRs.
- **Residual risk (accepted by Helms, 6:25 PM ET; R4-F4):** a same-named head job needs no token. GitHub Actions creates its check run itself, so a head workflow job named `agents-drift (base)` produces a second check with the required name. D20 catches it only inside the **real** base run, which goes red. Whether a red base run plus a green spoof satisfies the requirement is U18 (precedence UNVERIFIED).
- **Ruleset source pin (DEP-R1/DEP-R2):** set the source of every required check to **GitHub Actions**. That stops commit statuses or check runs from PATs and other apps (`cursor[bot]`, the Claude app), but it **cannot** tell a head `pull_request` job from the base `_target` job, because both are GitHub Actions. Whether source pinning is available on this plan for a user-owned private repo is UNVERIFIED, so the ruleset may not pin the source at all.
- **Backstop:** U18 (canary + spoof PRs) and reviewer discipline. **Reviewer rule (gate-3 checklist, `.github/AGENTS.md` links here):** "for any `.github/` PR, open both `(base)` checks and confirm they came from workflow `agents-governance`; two runs with one name = REQUEST CHANGES".
- The U18 spike also tries a commit-status spoof (`POST /statuses` with context `agents-drift (base)`).

## CODEOWNERS (advisory only, per `main`)

```
/.github/ @llevintza
/.github/workflows/agents-governance.yml @llevintza  # agents-governance.yml (planned; arrives with the harness build PR)
/AGENTS.md @llevintza
**/AGENTS.md @llevintza
/CLAUDE.md @llevintza
**/CLAUDE.md @llevintza
**/CLAUDE.local.md @llevintza
/.claude/ @llevintza
/.cursor/ @llevintza
/.cursorignore @llevintza
/.claudeignore @llevintza
/.grok/ @llevintza
/.mcp.json @llevintza
/.gitmodules @llevintza
/docs/agents/ @llevintza
/scripts/agents/ @llevintza
/.gitleaks.toml @llevintza
/perf/coverage-* @llevintza
/tests/testconfig.json @llevintza
**/Dockerfile* @llevintza
/deploy/ @llevintza
/render.yaml @llevintza
/docker-compose*.yml @llevintza
/compose*.y*ml @llevintza
/src/Desk.UserAdmin/ @llevintza
/src/Desk.Api/Auth/ @llevintza
/src/Desk.Data/Auth/ @llevintza
```

- **[TW]** `main` @ `99c0911e` (9:55 PM ET) has seven of these lines: `/.github/`, `/.claude/`, `/CLAUDE.md`, `/AGENTS.md`, `/.gitleaks.toml`, `/perf/coverage-*`, `/tests/testconfig.json`. The `**/AGENTS.md` and `**/CLAUDE.md` lines cover the seven new nested nodes (rev 11 adds `src/Desk.UserAdmin`).
- **Rev 11 (H19):** `/src/Desk.UserAdmin/`, `/src/Desk.Api/Auth/` and `/src/Desk.Data/Auth/` are **advisory** lines for the account and auth surface (UserAdmin can create admin accounts and, after #122, grant portfolios). They are visibility only and not governance paths. **They are not the barrier (rev 12, R11-F3):** no ruleset rule requires code-owner review, so they request review and enforce nothing. What protects production data and accounts is (a) the **governance-locked rules**: the root "local/test databases only" rule (root `AGENTS.md` is a governance path; **PENDING #123**: still open with no PR, so the rule is not in root at `9b83f7ef` or `5f91d26f`; Helms signed off the wording, #123 comment 6052455667; #123's merge is a build-PR hard precondition, rev 13 H20) and the nested `src/Desk.UserAdmin/AGENTS.md`, covered by `**/AGENTS.md`, so a PR editing it without a `[workflows]` title fails governance-paths (`testing.md`); and (b) **credential isolation**: production secrets live only in the `production` environment; UserAdmin is in neither `deploy.yml` nor `db-ops.yml` (governance paths), the runtime image or the db-tools bundle; agents never hold a Neon or production connection string (OQ-12; that README §12 line is a build-PR hard precondition, rev 13 H20); and the admin-session route is the tracked R6-M1 residual (OQ5). UserAdmin has no code-level connection-string guard at `9b83f7ef`, so a production connection string is its only way to production. The gate and Code Reviewer's review stay the process control.
- `/.gitleaks.toml`, `/perf/coverage-*` and `/tests/testconfig.json` are already on `main` since #5 (`741b19eb`; still present at `9b83f7ef`; #105 and #121 made raise-only `perf/coverage-baseline.json` bumps); the build work (Claude Code issue) keeps them.
- `/.cursor/` covers the generated `.cursor/hooks.json` (H8), and `.cursor/**` is on the governance-paths list, so every change to it is flagged and owned.

## Interim manual Gate-3 checklist (from CR round 3)

The `(base)` jobs can't bind the **bootstrap PR** (the PR that first puts `agents-governance.yml` (planned; arrives with the harness build PR) on `main`; ADR §8), because a `_target` workflow runs only from `main`. **Helms ruling (b), relayed by 6:21 PM ET:** the two-PR shape is confirmed; the bootstrap PR is governance-only, touches no app code, uses the arming rule, and gets Code Reviewer's full governance review as its human review. **Under Leo's hold (ADR §8 H18), the bootstrap PR and the build PR are each delivered as a GitHub issue that Claude Code implements; the preconditions and this review are unchanged.** Run the checklist on the bootstrap PR (item 3: "n/a, no restore block in this PR" if the block isn't in it) and again on the harness build work (Claude Code issue) as a second pair of eyes, even though the base checks bind that one. **Code Reviewer + TC** run this checklist and record the results in the PR body:

1. walk all 17 guards and D20 line by line against the real `agents-governance.yml` (planned; arrives with the harness build PR), including `package-manager-cache: false` (R3-F2);
2. run `actionlint` plus a pinned workflow security linter (e.g. `zizmor`) over every changed workflow;
3. confirm `review-restore-matrix` is green on the real block with **41/41, 14/14 and 18/18**;
4. review `pr-data.mjs`, `governance.mjs` and `check-drift.mjs` for any `import`/`require`/`eval`/`spawn`/`exec` reachable from PR data;
5. run head `agents-generate` + `agents-tests` with coverage ≥80% in the summary;
6. confirm no other workflow uses `pull_request_target`;
7. **once DEP-R1 exists:** run TC's ruleset check (below) immediately before the merge and record the result in the PR body; any drift means stop and escalate (H14).

Also record (HP, Helms SHA-pin FYI): every `uses:` in the changed harness workflows is pinned to a full 40-character SHA with a `# v…` comment (what D21 checks once on `main`).

**After the bootstrap merge: no steering freeze (H9, Helms 6:25 PM ET; resolves R4-F3).** The **only** frozen item is the harness build work (Claude Code issue), until the U18 canary passes and both base checks are in the ruleset (plus DEP-R1 verified with a by-name target and an empty bypass list, H12, H14, and Helms and TC's revisit of the R6-M1 admin residual with Tech Research's non-admin credentials proposal, R7-F2; ADR §8). **Rev 13 (H20, Helms adopting CR12 R12-N1):** it also does not merge until the OQ-12 README §12 credential-isolation line is on `main` and #123 has merged; TC confirms both on the build work's base (item 7 below). Normal app merges are not frozen. Any other agent-steering PR may merge in the window under this checklist when **all** of these hold:
- the title starts with `[workflows]`;
- it touches steering paths only (the governance-paths list), with no app code mixed in;
- Code Reviewer does a full governance review;
- nothing in it loosens Leo's gates or the review rules.

Once U18 passes, TC verifies Leo's `main` ruleset (DEP-R1, **PENDING**) and Leo adds both checks to it (DEP-R2), expect open PRs to go red until they merge `main`. A tightened base invariant turns every open PR red on its next event, and only a `main` merge fixes it (`edited` doesn't help). Announce tightenings to the TC.

## TC ruleset check before every merge and after every deploy (H14)

**Why:** every local agent session authenticates as `llevintza`, the repo admin (**rev 10:** Cursor cloud agents push as the Cursor GitHub App instead; it must not be on the bypass list either), so any agent session could disable, retarget or delete the `main` ruleset or change the default branch (R6-M1). Helms accepted that risk on condition of this check, the by-name target and the fail-closed admin denies. **Owners: Helms and TC** (R7-F2, confirmed with the round 7 rulings relayed ~7:52–7:53 PM ET; CR8 R8-N3). The structural fix (agent credentials without Administration) is a proposal Tech Research is drafting. **Hard precondition: Helms and TC revisit this risk with that proposal before the harness build work (Claude Code issue) lands** (ADR §8 freeze conditions). **Rev 13 (H20):** two more hard preconditions, the OQ-12 README §12 line on `main` and #123 merged, are checked in item 7.

**When:** at DEP-R1 verification; **immediately before every merge** (it is step 1 of TC's merge procedure, and item 7 of the Gate-3 checklist above); **after every deploy** (once `deploy.yml` finishes, or after any manual `deploy`/`db-ops` run); and after any break-glass.

**What (read-only; nothing is changed):**
0. **Precondition (rev 10): met.** Leo's account (the repo owner) is on GitHub Pro, which rulesets on this private repo need (ADR §8 H18 (2)). DEP-R1 itself stays **PENDING** until the ruleset is created and verified.
1. A ruleset for `main` exists and its enforcement is **Active**.
2. It **targets `refs/heads/main` by name** (include pattern `main`), not "Include default branch" or `~DEFAULT_BRANCH`.
3. The **bypass list is empty**.
4. The rules are present: restrict deletions; block force pushes; require a pull request (0 approvals); required checks: **every `ci.yml` job on the base** (rev 11, H19: base-relative, never a frozen sha; TC reads the job list from the base `ci.yml`; at `9b83f7ef`, for information only: `secrets`, `workflows`, `api`, `web`, `coverage`, `gate-tests`, `compose-smoke`, `budgets`, `db-tools`), plus DEP-R2's `agents-drift (base)` and `governance-paths (base)` once added; source GitHub Actions where available. Both directions match: every base `ci.yml` job is required, and every required check other than DEP-R2's is a base `ci.yml` job. No base `ci.yml` job has a job-level `if:` (rev 12, R11-F2).
5. The repository's **default branch is `main`**.
6. The ruleset's `updated_at` equals the value TC recorded last time (a change outside a recorded break-glass or a recorded ruleset sync is drift).
7. **Harness build PR only (rev 13, H20):** on the build PR's base, README §12 carries the OQ-12 line ("never export a Neon or production connection string in a shell that runs an agent session", or its merged wording), and root `AGENTS.md` carries #123's rules (E7 entitlements, Helms's verdict/sign-off rules, "agents never close issues", the UserAdmin line); TC records #123's merge commit. Either missing: the build PR does not merge.

**Ruleset sync (rev 12, R11-F2):** the ruleset lists required checks by literal name, so "every `ci.yml` job at the base" is kept true by hand. **Owner: Leo** (a normal ruleset edit, not break-glass). **Trigger:** any merge that adds, renames or removes a `ci.yml` job; Leo updates the ruleset, and TC's per-merge read-only check flags any drift between the base `ci.yml` job names and the required list. A rename or removal **fails closed**: the old required name never reports, so every PR blocks until Leo updates the ruleset. TC records the sync and the new `updated_at`, so it isn't read as drift. **Invariant:** no required job may be conditionally skippable while its peers pass (a skipped required check counts as passing). `coverage`, `compose-smoke` and `budgets` have `needs: [api, web]`, so `api` and `web` stay required and unconditional, and no `ci.yml` job gets a job-level `if:` (true at `9b83f7ef`; build-PR drift fixture in [`testing.md`](testing.md#dep-r1-required-check-invariant-rev-12-r11-f2)). Any drift in items 1–6: stop and escalate to Leo.

**How: TC's check commands** (read-only; canonical form only, H16 (4); they MUST stay allowed, and both deny lists and the guard allow them: fixtures in `testing.md`, `r9-benign`, `r9-claude-glob`):
- `gh ruleset list` (also `gh ruleset list --repo <o/r>`)
- `gh ruleset view <id>` (also `gh ruleset view <id> --repo <o/r>`)
- `gh repo view --json defaultBranchRef` (also `gh repo view <o/r> --json defaultBranchRef`)
- **Not** `gh rs …`, `gh -R <o/r> ruleset …` or any other flag before the verb: H16 blocks them as non-canonical (the alias `rs` is documented here so TC doesn't reach for it).
- or Settings → Rules → Rulesets and Settings → General.

Claude's project deny list blocks `gh api *rulesets*` (H14 over-deny), so TC doesn't use `gh api` for this from an agent session; the guard alone would allow a pure GET written endpoint first, such as `gh api repos/<o>/<r>/rulesets -X GET -f includes_parents=true` (the rev 8 spelling `gh api -X GET …` now blocks: flag before the endpoint, H16). Which of these shows the target, the bypass list and `updated_at` is UNVERIFIED; TC settles it at the first DEP-R1 verification and records the method here.

**Drift:** any mismatch means **stop and escalate**: no merge, TC pauses agent sessions, and TC tells Leo and Helms. Leo restores the setting; TC re-runs the check and notes the incident in the next PR body. A drift with no recorded break-glass is treated as a possible agent-session tamper (Residual 7).

### DEP-R1 "verified" today and under machine-user credentials (rev 10, TR finding 4)

- **Identities.** Local agent sessions authenticate as `llevintza` (repo admin). Cursor cloud agents push through the **Cursor GitHub App**, not as `llevintza`. The App's repo permissions are Administration **read** (it can't edit rulesets or branch protection) but Actions, Workflows and Merge-queues **write**, plus Contents, Pull-requests and Checks write, so the App is inside the R6-M1/OQ5 admin scope for Actions and Workflows writes (tied to H8, H15 and OQ5). DEP-R1's empty bypass list must explicitly exclude the App: no app or integration bypass actors.
- **Today (agents run as `llevintza`):** "verified" means TC's read-only check above passes: the ruleset exists, is Active and targets `refs/heads/main`; the bypass list is empty, including the Cursor App; the required checks are present; the default branch is `main`; `updated_at` is recorded.
- **Under Tech Research's non-admin machine-user credentials (OQ5):** a write-role reader can't see the bypass list or the ruleset history, so "verified" is redefined. **Leo does a one-time UI check** of the bypass list (empty, no app or integration actors). After that, TC's check before every merge and after every deploy confirms that the ruleset's `updated_at` hasn't changed since Leo's check, plus the fields the machine user can read. Any `updated_at` change means stop, escalate, and Leo re-checks.
- **Plan:** the GitHub Pro precondition is met; DEP-R1 stays **PENDING** until the `main` ruleset is created and verified.

## Rulings of 2026-10-07: PR #107 [TW]

Helms (CTO) made these rulings on 2026-10-07. PR #107 (head `8ad56687`) was merged to `main` as `79ceff6f` by Tech Coordinator under Helms's sign-off as Leo's delegate, so `main`'s root `AGENTS.md` already has this text. The text below is #107's, word for word. Issue #108 is now the #107 follow-up for Code Reviewer's fixes; this text follows #108 once it lands.

1. **Coverage baseline: a raise-only bump may ride in the feature PR.**
   > A measured, raise-only bump of `perf/coverage-baseline.json` may ride in the feature PR that earned it when all of these hold: the floor is read from **base** (not head); there are **no** threshold or measurement-scope changes; and Code Reviewer's governance check passes. Any **lowering** of `perf/coverage-baseline.json`, or any **measurement-scope** change, still needs its own `[workflows]` PR with an override record and sign-off from Code Reviewer, Tech Coordinator and Helms.

   The `governance-paths (base)` exemption that implements it is above (confirmed by Helms, 2026-10-07).
2. **Agents never force-push; a gitleaks finding means stop and report.**
   > The CI `secrets` job (gitleaks, `.gitleaks.toml`) scans the **full history** of every PR. Agents **never force-push**, including `--force-with-lease`. If gitleaks flags an agent's **own unmerged** commit, the agent **stops and reports to Tech Coordinator**. Rewrite (lease-protected, feature branch only) only when Tech Coordinator or Helms authorizes it. `main` is **never** rewritten.

   Rev 10 backs this in config: G9 blocks every force form, lease included; N10 and N64 become must-block; both deny lists gain the lease pattern (`MIN_DENY` 71 / `MIN_DENY_CURSOR` 70, re-verified for rev 10, `r10-verify.txt`). Branches are updated by merge, and `gh pr update-branch --rebase` is blocked by the guard only (G14; no deny-list entry). **Authorized rewrites (Helms, 2026-10-07):** An authorized rewrite is Leo running Tech Coordinator's exact commands (the branch, the expected old sha, and a lease-protected push of a feature branch only, never `main`) in a plain terminal, outside any harness session. There is no exception and no bypass; G9 still blocks every force form in agent sessions. Agents only stop and report (Helms, 2026-10-07, option (a)). A leaked secret must still be rotated: a rewrite doesn't un-expose a pushed secret (CR6 N3). Agent-facing text: [`workflow.md`](workflow.md).
3. **Delegated sign-off.**
   > Helms (CTO) signs off on `[workflows]` and rules PRs on Leo's behalf (delegated 2026-10-07); Leo can take any PR back for his own review.

   Wherever this page says a change needs Leo's review or sign-off (governance paths, "gate 3 + Leo"), this delegation applies. Break-glass is different: it is Leo's ruleset edit on his account (below), and #107 doesn't change that.

## H16: curl/wget and the gh allowlist (Helms's final call, 8:41 PM ET; binding)

- **`curl`/`wget` are denied outright in harness agent sessions, with no exception** (both deny lists and the guard; wrappers, absolute paths and any payload that mentions the words). **Rev 10 drops the health-script carve-out** (Helms H17 accepts the drop; CR9's finding against it is structural): there is no repo health script, and CI `compose-smoke`, the API smoke tests and the deploy smoke test cover health ([`workflow.md`](workflow.md#health-checks)). Docs: the harness web fetch tool (Claude Code `WebFetch`, Cursor web fetch); Grok Build only if Leo enables `web_fetch`; Copilot has none. Anything else that needs `curl` (installers, GHES, external APIs) is done by TC or a human outside the harness.
- **`gh` is an allowlist in canonical form** (`gh <group> <verb> …`, `gh api <endpoint> [flags]`). Read shapes and the PR/issue flows cloud agents use pass, including `gh pr update-branch` (merge form; **rev 10:** `--rebase` blocks), `gh api …/pulls/N/update-branch -X PUT`, reactions, and MCP `update_pull_request_base`; everything else blocks. **List-level regression (NIT):** the lists' `gh secret *`/`gh variable *` deny `gh secret list` and `gh variable list|get` (since rev 8), which the guard would allow; TC reads those in Settings or outside the harness.
- **Who does what is blocked:** label/release/gist/repo-settings commands, `gh auth token`, non-PR/issue API writes and GraphQL writes are TC's (or a human's), outside the harness.

## Actions writes: TC only, outside the harness (H15)

Workflow dispatch (`deploy.yml`, `db-ops.yml`), run rerun, run cancel and workflow enable/disable are inside H14's fail-closed scope for agent sessions under the repo harness (H15 (1), confirmed). Both deny lists and the guard block them in agent sessions (`gh workflow run|enable|disable`, `gh run rerun|cancel|delete`, `gh api …/dispatches` and `…/actions/…`, MCP dispatch tools). **Reruns and dispatches are TC's job, done from outside the harness** (the GitHub web UI, or a terminal that doesn't load this repo's harness). After any manual `deploy`/`db-ops` run, TC runs the ruleset check above.

## Hotfixes and production incidents (H14, H15, R6-F4)

- **Hotfix:** a normal PR from a branch, with Leo's gate and the required checks, and **no bypass** (H15 (2)). If CI or Actions is down, wait or use the incident path below; never edit the ruleset to ship a hotfix.
- **Production incident:** the incident path is **owned and coordinated by Helms and carried out by TC** (H15 (3)). Use **Render rollback** to the previous deploy. **`MAINTENANCE_MODE=true` is phase 2, not implemented today** (`deployment-setup.md` "Everyday operations": effective once phase 2 lands); **until then only Render rollback.** A rollback doesn't change `main`, and it doesn't undo a migration that `deploy.yml` already applied (H6); a forward fix is a hotfix PR.

## Break-glass: invariant changes only (H14, R6-F4)

Used only for a deliberate change that main's base checks reject (a loosened invariant, a removed subject, a shrunk `arming.json`, a gate-text change; ADR §5, `drift-checks.md` P, D17(d), D22). Never for hotfixes or incidents. **Authority: Leo** (his account, his decision; H15 (2)). Leo executes; TC pauses agent sessions and verifies.

1. The PR is reviewed as usual (governance review, Leo's gate) and its body states which invariant changes and why.
2. **TC pauses agent sessions** (no agent runs while the ruleset is relaxed, because every agent session is Leo).
3. **Leo edits the ruleset** for that one merge (e.g. removes the failing base check), **merges the one PR**, and **restores the ruleset** at once.
4. **TC re-verifies DEP-R1** with the check above (Active, target `main` by name, empty bypass list, all rules and checks, default branch `main`), records the new `updated_at`, and notes the break-glass (time, PR, what was relaxed) in the PR. Then agent sessions resume.

## H17: convergence and stopping rule (Helms, 10:03 PM ET; binding)

- Rev 10 fixes round 9's MAJORs, so ADR-0020 can be accepted. After that, a guard let-through found by a fresh probe is **not** an ADR finding: it is a **build-work fixture the guard must block** (`testing.md`), rated **FIXABLE if DEP-R1 is verified at merge, MAJOR if not**.
- **STRUCTURAL** issues stay **MAJOR and block the ADR**: anything that beats a control other than the guard's text matching (the ruleset, credentials, review restore, gate text, base-sourced checks, a PR changing its own rules, or the ADR's own spec text).
- **Exception:** a let-through that reaches an **admin endpoint** (ruleset, default branch, secrets/variables, Actions writes) stays MAJOR regardless of DEP-R1, until OQ5 removes agents' admin rights.
- R9 classification: M1 is a let-through (G20 blocks W01–W11; admin-endpoint test added); M2 is a let-through for pushes and MAJOR for admin writes (G21 kept; the renamed or copied binary reaching an admin endpoint rests on credentials: Residual 12, owners Helms and TC, revisit at OQ5); M3 is structural, so the health-script carve-out is dropped entirely.

## Baseline (rev 11)

- **Rev 13 re-check:** HEAD is `5f91d26f` (#140, 12:42 AM ET Oct 8). #140 changed no `AGENTS.md`, `.github/`, `ci.yml` or gate copy (README §17 rows only), so everything below still holds; the gate hash is unchanged (`r13-gate-diff.txt`).
- **`main` @ `9b83f7ef`** (#125, 11:43 PM ET; HEAD re-checked via cursor-github), with the gate text unchanged since **#107 @ `79ceff6f`** (10:12 PM ET). The rev 10 baseline was `c91c4b51` (#119, 10:38 PM ET); the rev 9 baseline was #104 @ `27c1c35a` (8:35 PM ET).
- **Since rev 10:** #121 `0cc350d7` (11:29 PM ET; Phase 3 positions API; README §17 + raise-only `perf/coverage-baseline.json` bump to 99.7/98.0) and #125 `9b83f7ef` (11:43 PM ET; `[workflows]` CI `budgets` job, AGENTS.md perf command rows, README §14.1 item). **Both merged outside the gate with no Code Reviewer verdict** (Helms, H19). Root `AGENTS.md` is 116 lines / metric 117. #6 merged earlier as `be83b190` (9:19 PM ET).
- Chain since #104 (rev 10 record): #96 `ea2b7dd1`, #100 `e75af6cd`, #103 `487236ca`, #99 `99c0911e`, #106 `4baeeb94`, #107 `79ceff6f`, #105 `5ce7f0ef`, #119 `c91c4b51`.
- #107 added to `AGENTS.md`: the raise-only coverage exemption, the force-push ban (incl. `--force-with-lease`), gitleaks stop-and-report, and Helms's delegated sign-off. #105 and #119 are feature PRs that touched no governance path except #105's README §17 Status row and a raise-only `perf/coverage-baseline.json` bump (dotnet line 99.1→99.6, branch 96.7→97.1; web unchanged).
- #108 is an **open issue** (re-sync the coverage config). If it lands, re-sync the quoted ruling text and keep `perf/coverage-override.json` in the enforced paths.
- The gate text (header and clauses 1–3) is **byte-identical** at `9b83f7ef` across README §14.5 (l.876–881), README §15 (l.916–921), both `AGENTS.md` copies, `docs/deployment-setup.md` step 5 (blob unchanged) and ADR §7 (canonical sha256 `f3e13ef3c8e54955`; `r11-gate-diff.txt`; at `c91c4b51`: `r10-verify.txt`).

