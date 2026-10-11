---
name: perf-budgets
description: "Measure README §10 budgets on a local stack: payload (Risk 60 KB), initial bundle < 500 KB br, k6 p95 MISS 150 ms / HIT 15 ms; write the before/after table."
paths:
  - "perf/**"
---
# Measure performance budgets

1. Start the local stack.
2. Create a throwaway viewer locally with Desk.UserAdmin.
3. Run `perf/payload-size.mjs`, the web build (bundle) and optionally k6 with the limit raised in the local process environment.
4. Fill the before/after table in the PR body.
5. Check the numbers against [`docs/agents/guidelines/perf-budgets.md`](../../../docs/agents/guidelines/perf-budgets.md).

The root local-only line applies. Do not restate it.

## Commands (local stack only; root AGENTS.md hard rules apply)

| Task | Command |
|---|---|
| Payload budget (phase 3) | `BASE_URL=… DESK_EMAIL=… DESK_PASSWORD=… node perf/payload-size.mjs` (CI `budgets` job; exit 1 over the Risk budget) |
| Last block + summary vs SQL (#43 AC4) | `BASE_URL=… DESK_EMAIL=… DESK_PASSWORD=… DATABASE_URL=… dotnet run -c Release --project perf/LastBlockCheck` (CI `budgets` job; exit 1 on any mismatch) |
| Grid benchmarks (ADR-0006/7/8) | `DATABASE_URL=… dotnet run -c Release --project perf/GridBenchmark -- 200 perf/out`, then `(cd perf && npm ci) && node perf/parse-bench.mjs perf/out` |
| API latency p95 (k6) | `docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=… -e DESK_EMAIL=… -e DESK_PASSWORD=… grafana/k6:1.3.0 run - < perf/positions.js` (local stack only: raise RATE_LIMIT_PER_USER_PER_MIN / RATE_LIMIT_PER_USER_BURST in that local process's environment for the run; never on Render, in render.yaml or the Render dashboard) |
