# Harness notes: Claude Code (local) and claude-code-action (CI review)

> Tech Writer draft for ADR-0020 (Revision 13). Repo path: `docs/agents/harness-claude-code.md` (new; adopted into ADR §1's tree, OQ-3). Harness-specific; the rules themselves live in `AGENTS.md` and [`workflow.md`](workflow.md). Full mapping tables: [`adapters.md`](adapters.md).

## What it reads (ADR §1 loading map; M§2, M§6, M§7)

| Surface | Loaded | Notes |
|---|---|---|
| Root `CLAUDE.md` → `AGENTS.md` | Always, at launch; re-injected after `/compact` | Claude reads AGENTS.md natively only when no `CLAUDE.md` exists (v2.1.277+). With the root stub present, AGENTS.md arrives through the import. VERIFIED (M§2). |
| `<area>/CLAUDE.md` → `<area>/AGENTS.md` | On demand, when Claude uses Read/Write/Edit on a file in that directory | Without the stub, a nested AGENTS.md is **not** read while a root `CLAUDE.md` exists (default mode; the mode switch can't be committed). VERIFIED (M§2). On-demand load under the action: UNVERIFIED (U11). |
| `.claude/settings.json` | Always | Permissions and hooks (below). |
| `.claude/skills/<name>/SKILL.md` | Listing always; body on use | Six skills (`roles-and-skills.md`). |
| `.claude/agents/<role>.md` | On use | Six roles (`roles-and-skills.md`). |
| `docs/agents/*` | Only when followed as a link | Never `@`-imported (N7, D16). |

## Thin adapter files (exact content)

Root and every nested area get the same one-line stub. Nothing else goes in it (D1, D2).

```text
@AGENTS.md
```

Paths: `CLAUDE.md`, `src/Desk.Api/CLAUDE.md`, `src/Desk.Data/CLAUDE.md`, `src/Desk.Seeder/CLAUDE.md`, `src/Desk.UserAdmin/CLAUDE.md` (rev 11, H19), `web/CLAUDE.md`, `.github/CLAUDE.md`, `deploy/CLAUDE.md`. Each is exactly that one line plus a newline (11 bytes, like `main`'s root stub).

Not allowed (D3, D5): `.claude/CLAUDE.md`, `CLAUDE.local.md`, `.claude/rules/`, a committed `.claude/settings.local.json`, nested `.claude/` directories. The build work (Claude Code issue) adds `CLAUDE.local.md` and `.claude/settings.local.json` to `.gitignore` (ADR §1).

## Permissions and hooks: `.claude/settings.json`

Draft file: `tree/.claude/settings.json` in the drafts folder (repo path `.claude/settings.json`). It is the rev 9 list from [`adapters.md`](adapters.md#permissions-mapping-claudesettingsjson-canonical--cursorclijson-gen) plus the **rev 10 lease deny**:

| Block | Entries | Enforces |
|---|---|---|
| deny, Read/Edit (8, in order; ruling 2) | `Read(**/.env)`, `Read(**/.env.*)`, `Read(!**/.env.example)`, `Read(**/secrets/**)`, then the same four for `Edit` | No secret reads or writes (U17 tests the `!` carve-out) |
| deny, push/merge (21) | 13 narrow push patterns (`main` destinations, `--force` as a whole word, `-f`, `--no-verify`) + 8 merge patterns | Never push to `main`; never merge |
| deny, bare push (1; H11) | `Bash(git push)` | Bare push; Claude only, D10-exempt |
| deny, admin + HTTP (28) | repo settings, admin `gh api` paths, `gh secret`/`gh variable`, workflow/run writes, `Bash(*curl*)`, `Bash(*wget*)` | TC-only actions; curl/wget denied (H16) |
| deny, flag-before-verb (14) | `Bash(gh -*)`, `Bash(gh pr -*)` … `Bash(gh api -*)` | gh canonical form (H16) |
| **deny, lease (1; rev 10)** | `Bash(git push *--force-*)` | Never force-push incl. `--force-with-lease`/`--force-if-includes` (PR #107). `MIN_DENY` 71, re-verified for rev 10 (`r10-verify.txt`). |
| ask (7) | `Edit(./**/AGENTS.md)`, `Edit(./**/CLAUDE.md)`, `Edit(./.claude/**)`, `Edit(./.cursor/**)`, `Edit(./.github/**)`, `Edit(./docs/agents/**)`, `Edit(./scripts/agents/**)` | Prompt before steering edits |
| allow (6) | `dotnet build *`, `dotnet test *`, `npm run lint`, `npm test *`, `git status`, `git diff *` | Common reads/tests without prompts |
| hooks | `PreToolUse` `Bash\|Shell\|Read\|Edit\|Write\|MultiEdit\|mcp__.*` → `node scripts/agents/hooks/guard.mjs`; `PostToolUse` `Edit\|Write\|MultiEdit` → `format.mjs`; `Stop` → `quick-test.mjs` | Guard (defense in depth), format, quick tests. **Rev 10:** the MCP part of the matcher (`mcp__.*` for Claude) is generated per adapter; UNVERIFIED until U22 |

- **MCP merge deny** (`mcp__*__merge_pull_request`) is left out of the draft JSON until U22 settles the exact tool names and wildcard support; the guard's `mcp__.*` matcher covers it meanwhile (also U22).
- **Web fetch:** Claude Code's built-in `WebFetch` is the docs path (H16 (3)). It prompts per domain except a preapproved set of docs domains. Optional `WebFetch(domain:<host>)` allow entries stay under D10's caps; the draft adds none.
- Claude matches deny rules per subcommand, after stripping `timeout`, `time`, `nice`, `nohup`, `stdbuf`, `command`, `builtin` and leading `VAR=` (documented, CR6 N4). `sudo`, `setsid`, `flock`, `env` still escape the lists; the guard covers them.

## claude-code-action (CI review)

- Loads **base** copies only: `claude-review.yml` restores base `AGENTS.md`/`README.md`, deletes nested memory and `.claude` files case-insensitively, then restores base `scripts/agents/` (ADR §4, §7; [`review-restore.md`](review-restore.md)).
- Hooks off (`--settings '{"disableAllHooks (planned; arrives with the harness build PR)": true}'`), Skill/Agent/Task denied, `Read(./.git/**)` denied, canary step fails the job if a hook ran (D12).
- Nested base stubs load on demand under the action: UNVERIFIED (U11); fallback: strip.

## Gaps and unverified behaviour

| Item | Status | Effect |
|---|---|---|
| U11 nested stub loads under the action | UNVERIFIED | Fallback: strip nested files in CI |
| U16 `--setting-sources user` keeps CLAUDE.md loading | UNVERIFIED | Keep `disableAllHooks (planned; arrives with the harness build PR)` only |
| U17 `!` carve-out in the installed version | UNVERIFIED (documented) | Fallback: bare `!.env.example`, else drop the `!` rules |
| U19 `disableAllHooks (planned; arrives with the harness build PR)` honoured in a real run | UNVERIFIED (documented) | CI no-op hooks + canary |
| U22 MCP tool names, wildcards, `mcp__.*` matcher | UNVERIFIED | MCP denies not counted |
| Hooks fail open on timeout | VERIFIED (M§2) | Guard is best-effort |
| Read deny doesn't cover commands that read without naming the file (`grep -r KEY .`) | VERIFIED (U13) | Real control: no secrets in the tree (D19, gitleaks) |
| `Bash(git push *--force-*)` | Rev 10 decision; count re-verified (`MIN_DENY` 71) | None for the count; the deny's own matching is checked by the golden file |
