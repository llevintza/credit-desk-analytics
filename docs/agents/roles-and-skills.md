# Roles, skills, `pr-ready` and generated headers

> Draft companion to ADR-0020 (Revision 13; points from revs 10–13 are marked **rev 10**, **rev 11**, **rev 12**, **rev 13**). Repo path: `docs/agents/roles-and-skills.md`. Tech Writer revision (2026-10-07, 10 PM ET) of the Architect's draft, which it supersedes; changes are marked **[TW]** and listed in the drafts README.

> **Restored in rev 5 (R4-F6).** Rev 4 dropped this detail instead of moving it. The text below is rev 3 verbatim (l.146–154, l.156–167 and l.169–189), with only these edits: headings demoted one level; cross-references renamed (§5 → `drift-checks.md`/`governance.md`, §6 → `adapters.md`, §7 → ADR §7, §8 → `testing.md`); the implementer row's push/merge note updated for H7 (narrow + guard in Cursor). Rev 6: the generator heading demoted to `###` so the roles sit beside it (CR5 N8); the implementer note adds Claude's exact bare deny (H11) and DEP-R1. Rev 7: the implementer note adds the admin denies (H14) and the push-steering line (CR6 N6).

### Generator and generated headers (rev 3 §2)

- **Generator:** `node scripts/agents/generate.mjs [--check] [--root <dir>]`. It uses `node:` builtins only, with no third-party dependencies (N2). The manifest `adapters.json` declares every output, its sources and its mode. It also declares the steering **input set** that `agents-drift (base)` fetches (`drift-checks.md`, `governance.md`).
- **Headers:**
  - `.md`/`.mdc` outputs start with `<!-- GENERATED from <source> by scripts/agents/generate.mjs — do not edit -->`, or the YAML-comment form inside the frontmatter.
  - `.cursorignore` and `secret-patterns.txt` start with `# GENERATED …`.
- **`.cursor/cli.json` (U8: STILL UNVERIFIED, decision kept):**
  - **Schema (TR):** the project file is `<repo>/.cursor/cli.json`; the global file is `~/.cursor/cli-config.json`. "Only permissions can be configured at the project level." The format is pure JSON (no comments).
  - **What the generator emits:** only `{"permissions":{"allow":[…],"deny":[…]}}`, with no in-file marker.
  - **Why no marker:** Cursor's handling of unknown keys in `cli.json` is undocumented. The docs also say "CLI performs self-repair for missing fields", and that files it treats as corrupted are backed up as `.bad` and recreated. A marker key could therefore cost every steering deny.
  - **Instead:** `scripts/agents/generated.lock.json` records `{path: sha256(sources)}`, and D14 checks it.

**Build-PR spec:** `.claude/agents/` is not in PR-A (tool lists are permission scopes).

### 3. Roles (`.claude/agents/*.md`)

Claude and Cursor read `.claude/agents/` natively (M§6); Grok support is UNVERIFIED (U4). Frontmatter is limited to `name`, `description`, `tools` (Claude), `skills` (Claude) and `readonly` (Cursor). **No `hooks:` or `allowed-tools:` keys** (D18). Fields a harness doesn't know are assumed ignored (U12).

| Role | Scope | Tools / permissions |
|---|---|---|
| `implementer` | One §15 phase on `phase-N/<slug>`: code, tests, ADRs, PR body. Never merges. | Project settings. Merge and push denies per `adapters.md`: client-side in Claude (incl. the exact bare `Bash(git push)`, H11); narrow steering plus the guard (best-effort) in Cursor (H7). Admin endpoints (rulesets, default branch, environments, secrets, `gh workflow run`) are denied and fail closed in the guard (H14). Pushes its branch with a separate `git push -u origin <branch>` (CR6 N6). **[TW] Never force-pushes, lease included; updates its branch by merge; stops and reports a gitleaks finding to TC (PR #107; rev 10; `workflow.md`).** The ruleset (by name, no bypass actors) is the real control once verified (DEP-R1). Preloads `pr-ready`. |
| `reviewer` | Diff vs Leo's gate + AGENTS/README. Posts nothing unless asked. | `Read, Grep, Glob, Bash(gh pr diff *), Bash(gh pr view *)`. `readonly: true`. |
| `architect` | ADRs and design notes in `docs/adr/**` and `docs/agents/**`. No app code. | `Read, Grep, Glob, Edit, Write`. The path scope is by instruction only. Preloads `adr`. |
| `test-writer` | `tests/**`, `web/**/*.spec.ts`. The gate-1 suites, with nothing skipped or weakened. | `Read, Grep, Glob, Edit, Write, Bash(dotnet test *), Bash(npm test *)` |
| `ef-migration` | `src/Desk.Data/**` contexts and migrations | `Read, Grep, Glob, Edit, Write, Bash(dotnet ef *), Bash(dotnet build *)`. Preloads `ef-migration-safety`. |
| `perf` | `perf/**`, README §10 budgets, before/after tables | `Read, Grep, Glob, Edit, Write, Bash(node perf/*)`. Preloads `pr-ready`. |

### 4. Commands and skills (`.claude/skills/<name>/SKILL.md`)

- **Why skills:** they are the only verified shared surface. `.cursor/commands` still exists but is **legacy**: the Cursor docs steer users to skills via `/migrate-to-skills` (U3, TR). `.claude/commands/` is legacy too (M§2). D8 forbids both, so neither can shadow a skill.
- **Frontmatter:** `name` equals the folder name. `description` is ≤ 200 characters, because every description is loaded on every turn. No `hooks:` or `allowed-tools:` (D18).
- **"User" skills** set `disable-model-invocation: true`, so only a person can invoke them.

`SKILL.md` is canonical; this table only names the skills (D8a).

| Skill | Invocation | Does | Sources |
|---|---|---|---|
| `start-phase` | user | Checks that Tech Coordinator started the phase and the previous PR merged. Branches `phase-N/<slug>` from fresh `origin/main`. Lists the phase's ADRs and DoD. | AGENTS item 7, README §15 |
| `pr-ready` | user | Pre-PR self-check (below) | Gate (ADR §7), README §10, §17 |
| `ef-migration-safety` | model + user | Expand → deploy → contract. Nullable/default columns. No rename/drop in the same release. No seed data in migrations. `has-pending-model-changes` false. Migrate twice. | README §14.4, ci.yml `api` |
| `seeding` | model | `--if-changed`. Bump `SeedVersion` when the generator or schema changes. COPY bulk load. < 90 s / < 350 MB, fails above 400 MB. Synthetic names only. | README §5.4–5.5 |
| `add-endpoint` | model + user | Add or change an `/api` minimal-API endpoint (OpenAPI, entitlements, cache, tests). | `src/Desk.Api/AGENTS.md`, `guidelines/dotnet-api.md` |
| `fix-review-feedback` | model + user | Fix Code Reviewer findings on the same branch; never resolve an unfixed thread; never post verdicts. | root review-gate bullets |
| `perf-budgets` | model + user | Measure README §10 budgets on a local stack and write the before/after table. | `guidelines/perf-budgets.md` |
| `adr` | model + user | Copies the template, takes the next free number (checks open PRs), adds the index row, includes measured numbers + a `perf/` script | AGENTS item 4, `docs/adr/README.md` |

**`pr-ready` steps:**
1. Build + test: `dotnet build`, `dotnet test`, `cd web && npm run lint && npm test -- --watch=false && npm run build`.
2. Coverage: ≥ 80% on new or changed code, and main never drops. Since #5 (`741b19eb`): `node perf/coverage-gate.mjs --dotnet TestResults/coverage --web web/coverage --base origin/main`.
3. Secrets: gitleaks with `--log-opts=origin/main..HEAD`. **[TW]** Run it with the same pinned image as `ci.yml` (`zricethezav/gitleaks:v8.30.1@sha256:c00b6bd0…`; OQ-11, adopted). A finding on your own commit: stop and report to TC; no rewrite, no push (`workflow.md`).
4. README §10 budgets: `node perf/payload-size.mjs`, initial JS bundle < 500 KB, before/after table. (local stack only, root rule).
5. PR hygiene: template, §17 row, ADR links. Steering or enforcement paths require a `[workflows]` title (exception: a measured raise-only `perf/coverage-baseline.json` bump in the feature PR that earned it, per PR #107; only line and branch values that go up or stay the same, same keys; a same-PR change to `perf/coverage-thresholds.json`, `perf/coverage-override.json`, `tests/testconfig.json`, another coverage measurement/threshold file or another governance path voids it; a non-line/branch field change or an unparseable file fails; confirmed by Helms, `governance.md`).
6. **[TW]** Commit messages in the quoted-heredoc form (`-F <file>` only for a file outside `.git/`); no curl/wget (no exception) or non-canonical gh anywhere in the commands (`workflow.md`).
