# Harness notes: Grok Build (and "Grok CLI", planned)

**NOT ENABLED (D7).** There is no `.grok/` in the repo. Do not add `.grok/` config, `[compat]` blocks or a Grok settings file until Grok is enabled (see Enablement steps).

Once enabled, Grok reads root and nested `AGENTS.md` plus `CLAUDE.md` in every directory from the repo root down to cwd (docs.x.ai project-rules). A possible double load of `@AGENTS.md` is U2.

It reads `.claude/skills/*` through Claude Code compatibility, and `.claude/agents` once the harness build PR adds them (planned; arrives with the harness build PR; U4).

It does **not** need `.agents/skills`. It reads `.cursor/rules/*.md`, and `.mdc` only if U5 holds.

## Enablement steps

Enablement needs its own settings source (residual 10: `Bash(git push)` is a prefix) and a `grok inspect` check. Until that source exists, Grok stays off.

## Gaps and unverified behaviour (kept from the TW draft)

| Item | Status | Effect |
|---|---|---|
| U2 `@AGENTS.md` expansion | UNVERIFIED | Double load or +1 line per stub. Check with `grok inspect` |
| U4 `.claude/agents/` | UNVERIFIED | Roles may be missing; generated fallback |
| U5 `.mdc` globs | UNVERIFIED | Fallback rules may be always-on in Grok |
