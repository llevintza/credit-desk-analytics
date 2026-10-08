# Harness notes: Grok Build (and "Grok CLI", planned)

> Tech Writer draft for ADR-0020 (Revision 13). Repo path: `docs/agents/harness-grok.md` (new; adopted into ADR §1's tree, OQ-3). Harness-specific; the rules live in `AGENTS.md` and [`workflow.md`](workflow.md). Source for every VERIFIED cell: the conventions matrix M§5 (docs.x.ai/build and `xai-org/grok-build`, read 2026-10-07).

**Which binary:** this page covers xAI's official **Grok Build** (binary `grok`). The community `superagent-ai/grok-cli` also installs `grok`, states it isn't affiliated with xAI, and isn't supported here (M§5; OQ-10, adopted). Check which one is on `PATH`. "Grok CLI" in planning notes means Grok Build's CLI.

## What it reads (M§5, M§7)

| Surface | Loaded | Status |
|---|---|---|
| `AGENTS.md` and `CLAUDE.md` in every directory from the repo root down to cwd | At start, in full, no size cap; deeper files take precedence | VERIFIED |
| Instruction files in other directories | When Grok reads, lists or edits files there | VERIFIED |
| `@AGENTS.md` inside a `CLAUDE.md` stub | Expanded or not? | **UNVERIFIED (U2)**. If expanded, each area file loads twice (root: about 2 × 100 lines with this draft); if not, each stub adds one line |
| `.claude/settings.json` permissions (`allow`/`ask`/`deny`) | Read, walking up to the repo root; any deny wins | VERIFIED. How Grok matches Claude's `Bash(...)` glob patterns (per subcommand, wrappers, `*curl*`) is **not documented in the inputs: UNVERIFIED** |
| `.claude/settings.json` hooks **and `.cursor/hooks.json` hooks** | Both read (C7; **rev 10**) | VERIFIED. The guard runs at most once per tool call (idempotent record, [`adapters.md`](adapters.md#hooks-claudesettingsjson)), so a second hook source can't run it twice. Whether Grok fires the Cursor-native `beforeShellExecution` entry is **UNVERIFIED** |
| `.claude/skills/` | Read (compat) | VERIFIED |
| `.claude/agents/` subagents | ? | **UNVERIFIED (U4)**; fallback: generated `.grok/agents/` (`grokAgents: true`) |
| `.cursor/rules/*.mdc` globs | ? | **UNVERIFIED (U5)**: the U1 fallback rules may load unconditionally in Grok |
| Folder trust | Startup loading needs `--trust` or an interactive grant; project hooks need `/hooks-trust` | VERIFIED. **Until hooks are trusted, the guard doesn't run in Grok** (enablement step 3) |

## Thin adapter files

- **None today.** Grok reads `AGENTS.md`, the `CLAUDE.md` stubs, `.claude/settings.json`, `.claude/skills` and the generated `.cursor/hooks.json` natively. No `.grok/` files: D7 forbids them except the generated `.grok/agents/*.md` fallback.
- **Precondition for enabling Grok (rev 10, Residual 10):** Grok treats `Bash(git push)` in `.claude/settings.json` as a **prefix**, so Claude's bare deny (H11) would block every Grok push. Before Grok is enabled, the Grok adapter needs **its own settings source that doesn't consume the bare `Bash(git push)` deny** (Grok has no exact-match). Owner Architect; decided at the Grok spike, together with the D7 change that source needs.
- No `.grok/config.toml` either: project config can carry only `[mcp_servers]`, `[plugins]` and `[permission]`, and the Claude permission list already covers this repo (M§5, M§6 item 10).

## Enablement steps

Leo or Tech Coordinator does these once on every machine that runs Grok Build in this repo (OQ-9, adopted as an enablement step). Grok stays disabled until the Grok adapter has its own settings source (above; ADR Residual 10).

1. Confirm `grok` on `PATH` is xAI's Grok Build, not the community CLI.
2. Trust the repo folder (`--trust`, or the interactive grant at start).
3. Run `/hooks-trust`, so the project hooks (the guard) run. Without it the guard is off in Grok.
4. Check with `grok inspect` that the `AGENTS.md` files, the permission lists and the hooks are loaded (the U2, U4 and U5 spikes use the same command).
5. Optional, Leo's call: turn on `web_fetch` in the user's `~/.grok/config.toml` (`[features] web_fetch = true`) or with `GROK_WEB_FETCH=1`. Without it, Grok sessions on that machine have no docs path.

## Permissions and the workflow rules

- Grok applies the same `.claude/settings.json` deny/ask/allow lists as Claude Code (see [`harness-claude-code.md`](harness-claude-code.md)), so curl/wget (no exception), flag-before-verb `gh`, merge, admin, narrow push and lease (**rev 10**) denies apply, **subject to the UNVERIFIED pattern-matching row above**.
- The guard runs as a `PreToolUse` hook (Grok's only blocking event): exit 2 denies. **Every other outcome fails open: timeouts (5 s default), crashes, malformed output** (M§5). **Rev 10:** the guard decides in ≤500 ms at p99 with a 1 s internal hard cap that fails closed (exit 2), well under Grok's 5 s timeout; the generated hook entry sets an explicit `timeout`. A harness timeout still fails open (Residual 11; owners Architect and TC; revisit at the Grok spike or if CI p99 exceeds budget). `format`/`quick-test` often exceed 5 s; harmless, they are advisory.
- **MCP (rev 10):** MCP matchers are generated per adapter. Grok's matcher keys on its `server__tool` names (`__`); the guard normalizes Grok's camelCase `toolName`/`toolInput`, `run_terminal_command` and `server__tool` shapes. Both are UNVERIFIED until U22.
- **Web fetch (H16 (3)):** Grok Build's `web_fetch` is **off by default** (`allow_local` defaults to false). Project config can't turn it on (enablement step 5). Until Leo enables it on a machine, Grok sessions there have no docs path: accepted over-block (ADR Residual 9; Leo ask in ADR §8).

## Gaps and unverified behaviour

| Item | Status | Effect |
|---|---|---|
| U2 `@AGENTS.md` expansion | UNVERIFIED | Double load or +1 line per stub. Check with `grok inspect` |
| U4 `.claude/agents/` | UNVERIFIED | Roles may be missing; generated fallback |
| U5 `.mdc` globs | UNVERIFIED | Fallback rules may be always-on in Grok |
| U20 `!` negation | UNVERIFIED | Over-deny: `.env.example` read-only |
| U22 MCP tool names in Grok hook input | UNVERIFIED | The generated matcher may not fire for MCP calls in Grok |
| Claude-pattern matching semantics in Grok | UNVERIFIED (not in M§5) | Treat the lists as steering; the guard and DEP-R1 back them |
| `.cursor/hooks.json` event mapping in Grok | UNVERIFIED | The idempotent guard (rev 10) makes a second hook source harmless |
| Hook trust (`/hooks-trust`) | VERIFIED requirement | No guard until trusted: enablement step 3 |
| Hooks fail open on any error or timeout | VERIFIED | Guard is best-effort; rev 10 budget ≤500 ms p99 / 1 s fail-closed cap; timeout fail-open is Residual 11 |
| Grok treats `Bash(git push)` as a prefix | Known (Tech Research) | Bare-push over-block; Grok needs its own settings source before enablement (Residual 10) |
| `web_fetch` off by default | VERIFIED | No docs path unless enabled (step 5) |
