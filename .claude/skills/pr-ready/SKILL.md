---
name: pr-ready
description: Pre-PR self-check: build, tests, lint, coverage gate, gitleaks on the branch, budgets on a local stack, PR template and [workflows] title. Run before opening or updating a PR.
disable-model-invocation: true
---
# Pre-PR self-check

Run before opening or updating a PR.

1. Build + test: `dotnet build`, `dotnet test -c Release`, `cd web && npm run lint && npm test -- --watch=false && npm run build`.
2. Coverage: ≥ 80% on new or changed code, and main never drops. `node perf/coverage-gate.mjs --dotnet TestResults/coverage --web web/coverage --base origin/main`.
3. Secrets: the pinned image from `ci.yml` `secrets`, branch commits only:

```
docker run --rm -v "$PWD":/repo zricethezav/gitleaks:v8.30.1@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f git /repo --config /repo/.gitleaks.toml --redact --no-banner -v --log-opts=origin/main..HEAD
```

A finding on your own commit: stop and report to Tech Coordinator; no rewrite, no push.

4. README §10 budgets on a local stack only (root rule): `node perf/payload-size.mjs`, initial JS bundle < 500 KB, before/after table.
5. PR hygiene: template, §17 row when the phase status changes, ADR links. Steering or enforcement paths require a `[workflows]` title (raise-only baseline exemption: root item 6).
6. Commit messages in the quoted-heredoc form (`-F <file>` only for a file outside `.git/`); no curl/wget (no exception) or non-canonical gh (`docs/agents/workflow.md`).
