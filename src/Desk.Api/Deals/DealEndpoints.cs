using System.Diagnostics;
using System.Text.Json;
using Desk.Api.Insights;
using Desk.Api.Positions;
using Desk.Data.Deals;

namespace Desk.Api.Deals;

/// <summary>
/// P4 Deal Explorer (README §6): every deal with one representative bond, and a deal's capital structure, surveillance
/// trend and the caller's positions in it. Cache-first; portfolio-owned numbers are scoped in SQL (ADR-0021).
/// </summary>
public static class DealEndpoints
{
    public static RouteGroupBuilder MapDealEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/deals", ListAsync)
            .WithTags("Deals")
            .WithName("GetDeals")
            .WithSummary("Every deal (including deals with no bonds yet) with its most senior bond and what the caller's portfolios "
                + "hold of it, for an as-of date.")
            .Produces<DealList>()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapGet("/deals/{dealId:int}", DetailAsync)
            .WithTags("Deals")
            .WithName("GetDeal")
            .WithSummary("One deal: its tranches (most senior first), the last 12 months of surveillance and the caller's positions in it.")
            .Produces<DealDetail>()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    internal static async Task<IResult> ListAsync(
        DateOnly? asOf, string? portfolioIds, HttpContext http,
        MetaCache metaCache, IPortfolioEntitlements entitlements, DealRepository deals, PositionsCache cache, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var (error, meta, date, portfolios) = await ScopeAsync(asOf, portfolioIds, http, metaCache, entitlements, ct);
        if (error is not null) return error;

        var etag = $"W/\"deals:{PositionsEndpoints.Hash($"{date:yyyy-MM-dd}:{meta!.DataVersion}:{string.Join(',', portfolios)}")}\"";
        if (CachedResponse.TryHit(http, cache, etag, "application/json", started) is { } hit)
            return hit;

        var dbStarted = Stopwatch.GetTimestamp();
        var rows = await deals.ListAsync(date, portfolios, ct);
        var dbMs = Stopwatch.GetElapsedTime(dbStarted).TotalMilliseconds;

        var serStarted = Stopwatch.GetTimestamp();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DealList(date, rows), DeskJsonContext.Default.DealList);
        return CachedResponse.Store(http, cache, etag, bytes, "application/json", meta.BatchEndsAt, dbMs,
            Stopwatch.GetElapsedTime(serStarted).TotalMilliseconds, started, rows.Length);
    }

    internal static async Task<IResult> DetailAsync(
        int dealId, DateOnly? asOf, string? portfolioIds, HttpContext http,
        MetaCache metaCache, IPortfolioEntitlements entitlements, DealRepository deals, PositionsCache cache, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var (error, meta, date, portfolios) = await ScopeAsync(asOf, portfolioIds, http, metaCache, entitlements, ct);
        if (error is not null) return error;

        var etag = $"W/\"deal:{dealId}:{PositionsEndpoints.Hash($"{date:yyyy-MM-dd}:{meta!.DataVersion}:{string.Join(',', portfolios)}")}\"";
        if (CachedResponse.TryHit(http, cache, etag, "application/json", started) is { } hit)
            return hit;

        var dbStarted = Stopwatch.GetTimestamp();
        var detail = await deals.DetailAsync(dealId, date, portfolios, ct);
        var dbMs = Stopwatch.GetElapsedTime(dbStarted).TotalMilliseconds;
        if (detail is null)
        {
            http.Response.Headers.Remove("ETag"); // nothing to revalidate
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such deal", detail: $"Deal {dealId} does not exist.");
        }

        var serStarted = Stopwatch.GetTimestamp();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(detail, DeskJsonContext.Default.DealDetail);
        return CachedResponse.Store(http, cache, etag, bytes, "application/json", meta.BatchEndsAt, dbMs,
            Stopwatch.GetElapsedTime(serStarted).TotalMilliseconds, started, detail.Tranches.Length + detail.Positions.Length);
    }

    /// <summary>
    /// The as-of date and the portfolio scope both deal endpoints share: requested ids outside the caller's
    /// entitlement are dropped, none requested means all of it (ADR-0021). Returns a problem result when invalid.
    /// </summary>
    private static async Task<(IResult? Error, MetaSnapshot? Meta, DateOnly Date, int[] Portfolios)> ScopeAsync(
        DateOnly? asOf, string? portfolioIds, HttpContext http, MetaCache metaCache, IPortfolioEntitlements entitlements, CancellationToken ct)
    {
        if (!InsightsEndpoints.TryParseIds(portfolioIds, out var requested))
            return (Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid portfolioIds",
                detail: "portfolioIds is a comma-separated list of integers."), null, default, []);
        var meta = await metaCache.GetAsync(ct);
        if (!meta.HasData)
            return (Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No data loaded",
                detail: "The database has not been seeded yet."), null, default, []);
        var date = asOf ?? meta.AsOfDates[0];
        if (!meta.AsOfDates.Contains(date))
            return (Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown as-of date",
                detail: $"No data for {date:yyyy-MM-dd}. See GET /api/meta/as-of."), null, default, []);
        var entitled = entitlements.For(http.User, meta);
        int[] portfolios = [.. (requested.Length > 0 ? requested.Where(entitled.Contains) : entitled).Distinct().Order()];
        return (null, meta, date, portfolios);
    }
}
