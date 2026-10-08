# Agent session workflow rules

> Tech Writer draft for ADR-0020 (Revision 13). Repo path: `docs/agents/workflow.md` (new; adopted into ADR §1's tree, OQ-3). Linked from the root `AGENTS.md` "Agent sessions" section; loads on demand only (no `@` imports, N7).

These rules apply to every agent session in this repo, in every harness (Claude Code, Cursor IDE/CLI/cloud agents, Grok Build, Copilot). The root `AGENTS.md` states each rule in one line; this page gives the exact forms, examples and the reasons. Where a statement depends on a decision ADR-0020 rev 10 made, it says **rev 10**.

**Enforcement, honestly (ADR-0020 §6 "Primary control", §8 H2):** the deny lists and the guard hook (planned; arrives with the harness build PR) are best-effort, local and partly unverified. The primary control is the `main` ruleset (by name, no bypass actors), which is **PENDING (DEP-R1)**; its plan precondition, GitHub Pro on the repo owner's account, is met (**rev 10**). Today nothing server-side stops a push to `main`. Follow the rule whether or not anything blocks you. If something blocks you, use the documented form below or ask Tech Coordinator; never try another spelling. **Rev 10 (H17 stopping rule):** a guard let-through found after rev 10 becomes a test fixture, unless it is structural or reaches an admin endpoint (those stay MAJOR). A let-through is never a permission.

## Pushing and history

The rulings (Helms, 2026-10-07; PR #107, merged to `main` as `79ceff6f`), word for word. Issue #108 is the #107 follow-up for Code Reviewer's fixes; this text follows it once it lands.

> The CI `secrets` job (gitleaks, `.gitleaks.toml`) scans the **full history** of every PR. Agents **never force-push**, including `--force-with-lease`. If gitleaks flags an agent's **own unmerged** commit, the agent **stops and reports to Tech Coordinator**. Rewrite (lease-protected, feature branch only) only when Tech Coordinator or Helms authorizes it. `main` is **never** rewritten.

| Rule | Source |
|---|---|
| Never push to `main`. `main` changes only by merging a reviewed PR. | AGENTS item 2 |
| Push your branch as its own command: `git push -u origin <branch>`. Name the branch; don't push `HEAD`, `@` or a bare `git push` after a branch switch in the same command. | ADR §6, Consequences (CR6 N6); guard over-blocks B18, B19 |
| **Never force-push, in any form:** `--force`, `-f` (also in bundles like `-uf`), `+<ref>`, `--force-with-lease`, `--force-if-includes`, and their abbreviations. Both deny lists and the guard block every form (G9). | #107; **rev 10** (G9, N10 and N64 must-block) |
| **`main` is never rewritten.** | #107; DEP-R1 "block force pushes" (PENDING) |
| Your duty on a gitleaks finding doesn't change: stop and report to Tech Coordinator. Never run a lease push yourself. An authorized rewrite is Leo running Tech Coordinator's exact commands in a plain terminal, outside any harness session (see "Authorized rewrites" below). | #107; Helms, 2026-10-07 |

Allowed:

```bash
git push -u origin phase-2/auth-and-limits
git push origin cursor/fix-coverage-gate
```

Not allowed (do not run):

```text
git push origin main                            # main
git push --force-with-lease origin feat         # force, any form
git checkout -b feat && git push origin HEAD    # chained HEAD push (guard over-block B18)
cd .. && git push                               # cwd change before a bare push (B19)
```

### Authorized rewrites (Helms, 2026-10-07, option (a))

- An authorized rewrite is Leo running Tech Coordinator's exact commands in a plain terminal, outside any harness session.
- Tech Coordinator's commands name the branch, the expected old sha, and a lease-protected push of a feature branch only, never `main`.
- There is no exception and no bypass. G9 still blocks every force form in agent sessions.
- Agents only stop and report. Never start a lease push, and never ask for a bypass.

**Cloud agents (rev 10):** Cursor cloud agents push as the Cursor GitHub App, not as `llevintza`. The rules are the same.

## Updating a pushed branch

- Branches are updated by merge. Rebasing your own branch on `origin/main` is fine **before your first push**. Once the branch is on the remote, bring in `main` with `git merge origin/main` (or `gh pr update-branch <n>`, which merges), never a rebase: publishing a rebase needs a force push.
- **`gh pr update-branch --rebase` is blocked** (rev 10; by the guard only, no deny-list entry): it rewrites the remote branch on the server, which is a force push by another route.
- PRs are squash-merged by default (AGENTS item 7), so merge commits on a feature branch don't reach `main`'s history.

## gitleaks findings

- **On your own unmerged commit:** stop and report to Tech Coordinator with the PR number, the commit SHA and the gitleaks rule id. Never paste the secret into the report. Don't rewrite the branch, don't add a commit that deletes the secret, don't push again, and never run a lease push. If Tech Coordinator or Helms authorizes a rewrite, Leo runs it in a plain terminal ("Authorized rewrites" above).
- **Rotate:** a rewrite doesn't un-expose a pushed secret (CR6 N3). Any leaked secret must be rotated; Leo or Tech Coordinator does that outside the harness.
- **On a commit that isn't yours, or one already on `main`:** also stop and report to Tech Coordinator (OQ-7, adopted).
- The CI `secrets` job scans the full PR history, so a follow-up commit never clears a finding.

## Commit messages

Use the quoted-heredoc form for every commit message; it is the standard (OQ-6). It is the one command-substitution form the guard allows in a commit command (ADR §6 G19), and it is Claude Code's default.

```bash
git commit -m "$(cat <<'EOF'
feat(api): add the positions summary endpoint

Summary row is an independent SQL aggregate over the same filter.
EOF
)"
```

- The delimiter is quoted (`<<'EOF'`), so nothing in the message is expanded. `EOF` stands alone on its line, then `)"` on the next.
- Subject in Conventional Commits style (AGENTS item 3).
- **`git commit -F <file>` is allowed only for a file outside `.git/`** (OQ-6), e.g. when the message mentions curl or wget. Never `-F .git/COMMIT_EDITMSG` or any other path under `.git/`.
- Put no other `$(…)`, backticks, `eval`, `xargs`, `| sh` or history reader (`git log`, `git show` …) in the same command as `git commit` (G19). `git commit -m x && echo $(git rev-parse HEAD)` blocks.
- Don't write `curl` or `wget` in a message passed on the command line: both deny lists and the guard match the words anywhere in the command (H16). Put such text in a file outside `.git/` and use `-F`.

## Health checks

There is no shell health probe for agents: curl and wget are denied with no exception, and no repo script wraps them (Helms H17 accepts dropping the carve-out; **rev 10**). Health is covered here instead:

- **API:** `SmokeTests` in `tests/Desk.Api.Tests` checks `/health` (status `ok` and a version, no database), that `/api` needs auth, that an unknown `/api` route is a 404, and the SPA fallback. It runs with `dotnet test -c Release`.
- **Postgres (local):** `docker compose ps` shows the `postgres` healthcheck (`pg_isready`).
- **Web:** the `npm start` output shows whether the dev server compiled; `cd web && npm run lint && npm test -- --watch=false` covers the code.
- **Whole stack:** the CI `compose-smoke` job on every PR.
- **Production:** the deploy pipeline's smoke test on `/health` after every merge (README §14.2).

If you need a running service checked by hand, ask Tech Coordinator.

## curl and wget

- **Denied outright in harness sessions, in every form, with no exception** (H16 (1); **rev 10** drops the old carve-out): wrappers, absolute paths, `sh -c`, `xargs`, aliases, interpreters, and any command that merely contains the word (`rg -n curl docs/` blocks; declared over-block).
- To search files for those words, use the harness's search tool (Claude `Grep`, Cursor search), not a shell command.
- PR bodies and comments that mention them: write the text to a file and use `gh pr create --body-file <file>` / `gh pr comment <n> --body-file <file>`.
- Installers, GHES and external APIs that need curl: Tech Coordinator or a human does them outside the harness (ADR Residual 9).
- **Hidden program names (rev 10, G20/G21):** CR round 9 found guard gaps for program names hidden behind globs, wrappers or copies (e.g. `xargs /usr/bin/c?rl`). Rev 10 blocks them: wrapped and glob program words fail closed (G20), and renamed or copied binaries are resolved by identity (G21). A copy made in an earlier session that reaches an admin endpoint remains a named residual (Residual 12). Either way the rule stands; a gap is never a permission.

## Fetching docs and web content

- Use the harness's built-in, read-only web fetch tool (H16 (3)): Claude Code `WebFetch`, Cursor's web fetch tool, Grok Build `web_fetch` (off by default; Leo enables it per machine, [`harness-grok.md`](harness-grok.md#enablement-steps)). Copilot has none.
- Never fetch through the shell: no curl/wget, no `node -e 'fetch(…)'`, no `python3 -c 'urllib…'` (interpreter HTTP is a named residual, not a permission).
- Where no fetch tool is enabled, docs are unreachable from the session; ask Tech Coordinator (ADR Residual 9).

## gh: canonical form only

**Canonical form** (ADR §6 G14, G15; §8 H16 (4); the §9 AGENTS line):

```text
gh <group> <verb> [arguments] [flags]
gh api <endpoint> [flags]
```

The group and the verb are the first two words after `gh`; for `gh api`, the endpoint comes first. Every flag, `-R`/`--repo` included, goes after them.

| Write this | Not this (blocks) |
|---|---|
| `gh pr view 96 -R llevintza/credit-desk-analytics` | `gh -R llevintza/credit-desk-analytics pr view 96` |
| `gh pr checks 96 --watch` | `gh pr --repo o/r checks 96` |
| `gh pr checkout 96` | `gh co 96` (alias) |
| `gh pr update-branch 96` | `gh pr update-branch 96 --rebase` (rewrite; rev 10) |
| `gh ruleset list` | `gh rs list` (alias) |
| `gh api repos/llevintza/credit-desk-analytics/pulls/96/comments --paginate` | `gh api --paginate repos/…/comments` |
| `gh api repos/o/r/issues/5 -X GET` | `gh api -X GET repos/o/r/issues/5` |
| `gh version` | `gh --version` (the deny lists' `gh -*` catches it) |

**Allowed verbs (guard allowlist, G14):** `pr view|list|status|checks|diff|create|edit|comment|review|ready|checkout|update-branch|close|reopen` (`update-branch` without `--rebase`); `issue view|list|status|create|comment|edit|close|reopen`; `run list|view|watch|download`; `workflow list|view`; `ruleset list|view|check`; `repo view|list|clone`; `release list|view|download`; `label list`; `cache list`; `search repos|issues|prs|code|commits`; `auth status`; `config get|list`; `alias list`; `extension list`; `gh status|help|version|completion`. Everything else blocks, including `pr merge`, `workflow run|enable|disable`, `run rerun|cancel|delete`, `secret|variable set|delete`, `repo edit|delete|rename|archive|fork|create`, `label create`, `release create`, `gist`, `auth token|login`, `alias set`.

**`gh api` (G15):**
- A pure GET (no `-X`/`--method` other than GET, no `-f`/`-F`/`--field`/`--raw-field`, no `--input`) may use any path, query string included: `gh api 'repos/o/r/pulls?state=open&per_page=50'`.
- Any other call needs a plain path (no `?`, `#`, `%`, `..`, `//`, backslash, whitespace or variable) on the PR/issue write allowlist (`pulls`, `pulls/N`, `pulls/N/{comments,reviews,requested_reviewers,update-branch}`, review and comment replies, `issues`, `issues/N`, `issues/N/{comments,labels,assignees,reactions}`, comment reactions), and no method-override header. (The REST `update-branch` endpoint only merges; the rebase method exists only in the GraphQL mutation, which is blocked as a mutation.)
- GraphQL: literal read queries only (`gh api graphql -f query='query { … }'`). No mutations.

**Over-deny you will hit:** Claude's deny list also blocks harmless reads that contain an admin word (`gh api …/rulesets`, `…/protection`, `…/environments`, `…/secrets`, `…/keys`, `…/actions/…`) and `gh secret list` / `gh variable list|get`. Tech Coordinator does those reads.

**Hidden `gh` names (rev 10):** wrapped, glob or copied `gh` binaries (`xargs /usr/bin/g[h] …`, a copied `gh`) are resolved and fail closed under G20/G21, then checked against this allowlist. The canonical form and the verb list are H16/G14 rules.

## Who signs off

- Gate: Tech Coordinator plus Code Reviewer (AGENTS item 6). Tech Coordinator merges.
- `[workflows]` PRs are reviewed by hand by Tech Coordinator and Leo. Helms (CTO) signs off on `[workflows]` and rules PRs on Leo's behalf (delegated 2026-10-07); Leo can take any PR back for his own review.

## Things only Tech Coordinator (or a human) does

From outside the harness (ADR §8 H15; [`governance.md`](governance.md)):
- merge PRs, by any route (gh, API, MCP, web UI);
- dispatch, rerun, cancel, enable or disable workflows (`deploy.yml` and `db-ops.yml` act on production);
- read or change rulesets, branch protection, the default branch, environments, secrets, variables, deploy keys, collaborators, webhooks;
- create labels, releases or gists; anything that needs curl/wget; secret rotation; an authorized history rewrite (Tech Coordinator writes the exact commands; Leo runs them in a plain terminal, outside any harness session).

## Other command shapes the guard rejects

These fail closed in the rev 9 guard (G4, G13, G19b). Rewrite them; don't work around them.

| Blocks | Write instead |
|---|---|
| `git log --oneline $(git merge-base HEAD origin/main)..HEAD` | `git merge-base HEAD origin/main`, then `git log --oneline <sha>..HEAD` |
| `"$EDITOR" notes.md`, `$(npm bin)/eslint .` | Name the program, or use the `npm run` script |
| `git log --format=%H -3 \| xargs -n1 git show --stat` | One `git show --stat <sha>` per commit |
| `bash some-script.sh` in a command that also pushes or merges | Run the script on its own |
| `cat .env`, `source .env`, `printenv` | Don't read `.env`. The human exports the variables before the session starts, and tests use Testcontainers (OQ-12). A Neon or production connection string is never exported in a shell that runs an agent session; that README §12 line must be on `main` before the harness build work merges (**rev 13**, ADR §8 H20) |

## Where these rules come from

| Rule | ADR-0020 | Other source |
|---|---|---|
| No push to `main`; branch push form | rev 9 §6, §8 H7/H11, Consequences (CR6 N6) | AGENTS item 2 |
| Never force-push incl. lease; `main` never rewritten; update by merge; no `update-branch --rebase` | **rev 10** (G9; N10, N64 must-block) | PR #107 |
| gitleaks: stop and report to TC; rotate | **rev 10** | PR #107; CR6 N3 |
| Authorized rewrite: Leo runs TC's exact commands in a plain terminal, outside any harness session; no exception, no bypass | rev 10 G9 unchanged | Helms, 2026-10-07, option (a) |
| curl/wget denied, no exception; web fetch | rev 9 §6 G18, §8 H16 (1), (3); **rev 10** drops the carve-out (H17) | none |
| gh canonical form | rev 9 §6 G14/G15, §8 H16 (4), §9 | none |
| Heredoc commit form; `-F` outside `.git/` | rev 9 §6 G19 (exemption) | Architect, OQ-6 |
| Helms signs off `[workflows]` and rules PRs | none | PR #107 |
| TC-only actions | rev 9 §6, §8 H14 (4), H15 | `governance.md` |
