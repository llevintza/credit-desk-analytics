# Performance budgets (README §10)

Linked from the `perf-budgets` skill. Local stack only (root line).

## Payload and bundle

- Risk fails at 60 KB, All warns at 250 KB (`perf/payload-size.mjs`, CI `budgets` job at scale 1.0).
- Initial JS < 500 KB Brotli (`web/scripts/bundle-budget.mjs`).

## Latency

- k6 p95 MISS < 150 ms, HIT < 15 ms (`perf/positions.js`: 1 VU, 30 s per scenario, local and warm, `grafana/k6:1.3.0`).
- Raise limits only in that local process's environment (root k6 row).

## Seeder and benches

- Seeder < 90 s and < 350 MB; it fails above 400 MB.
- `perf/GridBenchmark` + `parse-bench.mjs` (ADR-0006/7/8).
- `perf/LoadBenchmark` (ADR-0004; drops a `bench` schema).
- `auth-overhead.mjs`, `swagger-impact.sh`.

## PR body

- Report before/after tables. For ADR numbers, commit the script under `perf/`.
