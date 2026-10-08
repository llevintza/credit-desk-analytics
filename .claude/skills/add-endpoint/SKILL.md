---
name: add-endpoint
description: Add or change an /api minimal-API endpoint: OpenAPI metadata, ProblemDetails, CancellationToken, IPortfolioEntitlements scoping, ETag/cache, tests and coverage.
paths:
  - "src/Desk.Api/**"
---
# Add or change an /api endpoint

1. Read [`src/Desk.Api/AGENTS.md`](../../../src/Desk.Api/AGENTS.md) and [`docs/agents/guidelines/dotnet-api.md`](../../../docs/agents/guidelines/dotnet-api.md).
2. Add `MapX` on the feature group with OpenAPI metadata (`WithName`, `WithSummary`, `WithTags`, `Produces` / `ProducesProblem`).
3. Handler: entitlement scope (root E7), `Results.Problem` for errors, `CancellationToken` last.
4. Register new DTOs in `DeskJsonContext`.
5. Add a Testcontainers test in `Desk.Api.Tests` (the E7 stub cases from the root rule) and check `OpenApiTests` passes.
6. Coverage ≥ 80 on the diff.
7. Run `pr-ready`.

Fund aggregates / E8: [issue #124](https://github.com/llevintza/credit-desk-analytics/issues/124) only. Do not invent E8 text.
