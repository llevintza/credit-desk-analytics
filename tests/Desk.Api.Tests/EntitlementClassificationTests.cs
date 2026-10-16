using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Desk.Api.Positions;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Api.Tests;

/// <summary>
/// ADR-0021 R4: every <c>/api</c> path in the OpenAPI document is classified as scoped (it returns portfolio-owned data and
/// resolves its scope through <see cref="IPortfolioEntitlements"/>) or exempt (it returns no portfolio-owned data). A new
/// path fails here until it is classified; adding an exempt path needs an ADR-0021 amendment.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed partial class EntitlementClassificationTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Portfolio-owned data, scoped through <see cref="IPortfolioEntitlements"/>.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Scoped = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/api/positions/query"] = "positions; portfolio_id = ANY(@portfolios) in GridSqlBuilder, set in every cache key and ETag",
        ["/api/positions/export"] = "positions CSV; the same GridSqlBuilder predicate",
        ["/api/meta/portfolios"] = "the entitled portfolio list; filters the unscoped list through IPortfolioEntitlements",
        ["/api/funds/{fundId}/performance"] = "fund roll-up; all-or-nothing gate on the fund's portfolios (ADR-0021 amendment 2026-10-09)",
        ["/api/insights/{source}"] = "aggregates of positions; portfolio_id = ANY(@portfolios), set in the cache key and ETag",
        ["/api/deals"] = "deal holdings; portfolio_id = ANY(@portfolios) (ADR-0021 amendment 2026-10-15)",
        ["/api/deals/{dealId}"] = "deal detail positions; portfolio_id = ANY(@portfolios) (ADR-0021 amendment 2026-10-15)",
        ["/api/marks/stream"] = "intraday marks for the bonds the caller's portfolios hold; portfolio_id = ANY(@portfolios) in the universe read (ADR-0015)",
        ["/api/marks/totals"] = "overlay totals of positions; the GridSqlBuilder predicate and the scoped universe (ADR-0015)",
    };

    /// <summary>No portfolio-owned data at all. Adding an entry after #122 needs an ADR-0021 amendment.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/api/meta/as-of"] = "snapshot dates only; loses the exemption if it ever returns per-portfolio dates or ids (ADR-0021)",
        ["/api/meta/columns"] = "column catalog metadata, no rows",
        ["/api/presets/{page}"] = "the caller's own column presets, scoped by user; a preset's portfolio ids are re-intersected when it runs a query",
        ["/api/auth/login"] = "issues tokens for the caller's own account",
        ["/api/auth/refresh"] = "renews the caller's own tokens",
        ["/api/auth/logout"] = "ends the caller's own session",
        ["/api/me"] = "the caller's own email, roles and expiry",
        ["/api/admin/cache/clear"] = "admin action; returns no data",
        ["/api/admin/usage"] = "audit counts per user, route templates and timings; no portfolio ids or positions (ADR-0021 amendment 2026-10-16)",
    };

    [GeneratedRegex(@"\{(\w+):[^}]+\}")]
    private static partial Regex RouteConstraint();

    private async Task<string[]> OpenApiPathsAsync()
    {
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/openapi/v1.json", Ct));
        return [.. doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).Where(p => p.StartsWith("/api/", StringComparison.Ordinal))];
    }

    [Fact]
    public async Task Every_api_path_is_classified_scoped_or_exempt_exactly_once()
    {
        var paths = await OpenApiPathsAsync();
        Assert.Empty(Scoped.Keys.Intersect(Exempt.Keys));
        var unclassified = paths.Where(p => !Scoped.ContainsKey(p) && !Exempt.ContainsKey(p)).ToList();
        Assert.True(unclassified.Count == 0,
            "Classify each new /api path in EntitlementClassificationTests (ADR-0021 R4): Scoped if it returns portfolio-owned data "
            + "(resolve the scope through IPortfolioEntitlements and apply it in SQL), Exempt only with an ADR-0021 amendment:\n  "
            + string.Join("\n  ", unclassified));
        // A stale entry would let a later endpoint of the same path through unreviewed.
        Assert.All(Scoped.Keys.Concat(Exempt.Keys), p => Assert.Contains(p, paths));
    }

    [Fact]
    public void Scoped_handlers_take_the_entitlement_provider_and_exempt_ones_do_not()
    {
        // Each scoped path's handler resolves IPortfolioEntitlements; an exempt one has no use for it.
        var handlers = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true && e.Metadata.GetMetadata<MethodInfo>() is not null)
            .Select(e => (Path: RouteConstraint().Replace(e.RoutePattern.RawText!, "{$1}"), Method: e.Metadata.GetMetadata<MethodInfo>()!))
            .ToList();
        static bool TakesEntitlements(MethodInfo m) => m.GetParameters().Any(p => p.ParameterType == typeof(IPortfolioEntitlements));

        foreach (var path in Scoped.Keys)
        {
            var mine = handlers.Where(h => h.Path == path).ToList();
            Assert.True(mine.Count > 0, $"no handler for {path}");
            Assert.All(mine, h => Assert.True(TakesEntitlements(h.Method), $"{path} ({h.Method.Name}) is scoped but doesn't take IPortfolioEntitlements"));
        }
        foreach (var path in Exempt.Keys)
            Assert.All(handlers.Where(h => h.Path == path), h => Assert.False(TakesEntitlements(h.Method), $"{path} ({h.Method.Name}) takes IPortfolioEntitlements: classify it as scoped"));
    }
}
