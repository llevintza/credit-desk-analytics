# .NET 10 minimal APIs

Linked from [`src/Desk.Api/AGENTS.md`](../../../src/Desk.Api/AGENTS.md) and the `add-endpoint` skill. Root rules still apply (OpenAPI metadata, CancellationToken, entitlements, no secrets).

## Runtime

- .NET 10 (`global.json` SDK 10.0.401). `Directory.Build.props`: net10.0, Nullable, TreatWarningsAsErrors, InvariantGlobalization.

## Shape

- Minimal APIs by feature: `public static class XEndpoints` with `MapXEndpoints(this RouteGroupBuilder api)` (see `src/Desk.Api/Positions/PositionsEndpoints.cs`).
- Mapped in `Program.cs` on `app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<AntiforgeryFilter>()`.
- Handlers are `internal static async Task<IResult>` with DI parameters and the `CancellationToken` last.

## OpenAPI (ADR-0019)

- `MapGroup(...).WithTags`, `.WithName`, `.WithSummary`, `.Produces` / `.ProducesProblem`.
- Fallbacks use `ExcludeFromDescription()`. Guarded by `tests/Desk.Api.Tests/OpenApiTests.cs`.

## Errors

- `AddProblemDetails` + `UseExceptionHandler` + `UseStatusCodePages`.
- Return `Results.Problem(statusCode, title, detail)`.

## Entitlements and cache

- Scope: `IPortfolioEntitlements.For(http.User, meta)` (`Positions/PortfolioEntitlements.cs`; `AllPortfolios` is a singleton). Follow the root E7 rule; do not restate it.
- The entitled set feeds the normalized query whose canonical key is hashed into the ETag and cache key.
- Cache: weak ETag `asOf:DataVersion:hash:repr`; `Cache-Control: private, no-cache`; `Vary: Accept`.
- If-None-Match returns 304 with no DB read. MemoryCache entries are sized and expire at `meta.BatchEndsAt`.

## Observability and JSON

- `Server-Timing` and `X-Cache` (`SetTiming`).
- JSON uses source generation (`DeskJsonContext.cs`, inserted in `TypeInfoResolverChain`); register new DTOs there.

## Pipeline

- Order comment in `Program.cs`: maintenance mode before DB, limiter before audit.
- Rate limits come from `Limits/LimitsOptions.cs`. They stay on (root free-tier rule).
