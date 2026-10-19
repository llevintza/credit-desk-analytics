# ADR-0020: Harness-neutral agent setup (one rules source, generated adapters, base-sourced drift checks, a local guard hook)

- **Status:** Accepted, revision 14 (Helms, 2026-10-16, H21; the harness build was authorized by Leo and landed as #171 Stage A and Stage B)
- **Date:** 2026-10-19
- **Phase / PR:** `[workflows]` cross-cutting (#171; Stage A #498–#507, Stage B #508–#529 and B8)

This record is deliberately short. The design detail, the review history (revisions 1–14) and the evidence live in [`docs/agents/`](../agents/README.md), which is canonical for everything below.

## Context

Several coding agents (Claude Code, the Cursor IDE, the Cursor CLI and cloud agents, and possibly Grok Build later) work in this repo. All of them authenticate as the repo admin. Each one reads its own rules files, permission settings and hooks. Kept by hand, those copies drift, and a drifted copy can quietly drop a deny. A PR can also edit the very rules and checks that judge it. Every bot acts as the owner, so GitHub can't require an approving review, and the `main` ruleset (DEP-R1) is the only server-side control.

## Options considered

1. **Per-harness hand-written files:** `CLAUDE.md`, `.cursor/rules`, `.cursor/cli.json` and settings, each kept by hand.
2. **One rules source plus generated adapters:** `AGENTS.md` (root and area files) is the only rules text, with thin `CLAUDE.md` import stubs. `scripts/agents/generate.mjs` renders the Cursor permissions, `.cursorignore`, the guard's secret patterns and `.cursor/hooks.json` from `adapters.json`. Drift checks run from the **base** branch on the PR's data, and a local guard hook backs up the deny lists.
3. **Option 2 without the local guard:** rely on the deny lists and the ruleset alone.

## Evaluation

| Criterion | 1 Hand-written | 2 Generated + base checks + guard | 3 No guard |
|---|---|---|---|
| Rules copies kept in step | by hand | 1 source; regen-diff in `agents-drift (base)` and `agents-generate` | same as 2 |
| A PR weakening its own checks | possible | base-sourced (`_target`, main's copy of the scripts) | same as 2 |
| Deny floor on first introduction | none | `MIN_DENY` = 71 (Claude), `MIN_DENY_CURSOR` = 70 (Cursor), `MAX_ALLOW` = 6 | same as 2 |
| Push/merge/admin shapes the lists miss (wrappers, `$()`, aliases, `+<ref>`) | missed | guard fails closed on them (G1–G22) | missed |
| Guard cost per tool call | n/a | p50 33 ms, p99 48 ms per fresh-node call (budget p99 ≤ 500 ms, 1 s hard cap) | n/a |

**How to reproduce:** `node perf/guard-timing.mjs` (80 fresh-node calls), and the `agents-tests` command in [`.github/AGENTS.md`](../../.github/AGENTS.md).

```
$ node perf/guard-timing.mjs
guard hook, fresh node per call: n=80 p50=33 ms p99=48 ms max=48 ms (budget p99 ≤ 500 ms)
```

## Decision

Option 2. `AGENTS.md` is the single rules source. The Cursor adapters are generated and locked (`generated.lock.json`, D14). `agents-drift (base)`, `governance-paths (base)` and `ci-wiring (base)` judge each PR with main's scripts. `.claude/settings.json` carries the deny and allow lists, and its hooks run `scripts/agents/hooks/guard.mjs` (`PreToolUse`), `format.mjs` (`PostToolUse`) and `quick-test.mjs` (`Stop`), anchored on `${CLAUDE_PROJECT_DIR:-.}`. The guard fails closed at the harness layer too (`onFailure: "block"`, and `failClosed: true` in the generated `.cursor/hooks.json`; #528, #529), and D18 allows only those exact hook entries. The guard is best-effort defense in depth. The primary control remains the `main` ruleset with no bypass actors (DEP-R1).

**Revision 14 (H21):** the "(planned; arrives with the harness build PR)" marker convention is closed. The build removed every marker in the PR that landed its piece, so `git grep -n -E ' \(planned; arrives with the harness build PR'` returns nothing. No page under `docs/agents/` refers to a drafts-only file.

## Consequences

- One edit to `AGENTS.md` or `.claude/settings.json` reaches every harness. `node scripts/agents/generate.mjs` regenerates the rest, and `--check` fails CI on drift.
- A deny can be added but never narrowed below the floors. That includes the substring denies that also catch harmless read flags; [`docs/agents/workflow.md`](../agents/workflow.md) lists the read-only alternatives.
- The hooks fire only on agent tool calls, never on a person's own edits; Leo can switch them off locally (`workflow.md` "Switching the hooks off"), which removes only the guard, not the deny lists or the ruleset.
- The guard runs on every tool call in local sessions, and it over-blocks some benign shapes by design (`workflow.md` "Other command shapes the guard rejects"). It never runs in CI: `claude-review` disables hooks and checks the hook canary.
- Open items stay tracked in the U-table and the DEP rows ([`docs/agents/README.md`](../agents/README.md)): U9 (Cursor cloud agents and `.cursor/hooks.json`, with fallback (ii) if the spike fails), U21/U22 (Cursor hook shapes, MCP matchers), DEP-R2 (the base checks as required checks). Grok stays off until it has its own settings source.
- Revisit when DEP-R1 or DEP-R2 changes, when the U9 spike reports, or when agents get credentials without Administration permission (the H14 structural fix).
