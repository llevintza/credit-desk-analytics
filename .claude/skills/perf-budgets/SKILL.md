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
