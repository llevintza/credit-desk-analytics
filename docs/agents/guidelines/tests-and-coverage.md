# Tests and coverage gates

Linked from [`tests/AGENTS.md`](../../../tests/AGENTS.md). Root item 6 and gate clause 2 still apply.

## CI `coverage` job

- Floor, thresholds and the gate script come from the **base** sha (`_base`).
- A missing `BASE_SHA` or missing gate fails closed.
- Thresholds (`perf/coverage-thresholds.json`): diff line/branch ≥ 80, overall must not drop, tolerance 0.5.
- The raise-only `perf/coverage-baseline.json` exemption and the rules for lowering or measurement-scope changes are in root item 6; follow them there.

## Coverlet

- Flags are exactly as in `ci.yml` `api` (coverlet.MTP). Do not use `--collect "XPlat Code Coverage"`.
- Exclusions live in `tests/testconfig.json`. Never add `CompilerGeneratedAttribute` (it strips `Program.cs` lambdas).

## Vitest and the gate script

- Vitest via `@vitest/coverage-v8`. `cd web && npm test -- --watch=false --coverage`.
- `gate-tests` job: `perf/coverage-gate.test.mjs` at ≥ 80/80.

## Nothing skipped or weakened

- xUnit v3: no `[Fact(Skip = …)]`, no disabled collections, no weakened asserts to get green.
- Vitest: no `.skip` / `.only` left in committed specs.
- Docker is required for Testcontainers; never skip those tests.
