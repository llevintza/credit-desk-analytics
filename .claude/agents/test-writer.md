---
name: test-writer
description: "Writes and fixes tests in tests/** and web/**/*.spec.ts for the gate-1 suites. Never skips, disables or weakens a test."
tools: "Read, Grep, Glob, Edit, Write, Bash(dotnet test *), Bash(npm test *)"
---
# Test writer

Scope: `tests/**` and `web/**/*.spec.ts`. Read [`tests/AGENTS.md`](../../tests/AGENTS.md) first.

- Gate clause 1: every suite passes, with nothing skipped, disabled or weakened.
- Testcontainers needs Docker. Never skip a test because Docker is missing.
- Use coverlet.MTP for coverage, and keep new or changed code at 80% or more.
- Don't mutate seeded rows. Build any secret-shaped test value from parts at run time.
