using System.Diagnostics;
using System.Text.Json;
using Desk.Api.Audit;
using Desk.Api.Positions;
using Desk.Data.Funds;
using Microsoft.Extensions.Caching.Memory;

namespace Desk.Api.Funds;

/// <summary>P2 Fund Performance (README §6): balance and IRR by month-end for a range, cache-first.</summary>
public static class FundEndpoints
{
    public static RouteGroupBuilder MapFundEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/funds/{fundId:int}/performance", PerformanceAsync)
            .WithTags("Funds")
            .WithName("GetFundPerformance")
            .WithSummary("Balance and IRR by month-end for QTD | YTD | 1Y | ITD | CUSTOM (from, to). Months and rows always line up.")
            .Produces<FundPerformance>()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    internal static async Task<IResult> PerformanceAsync(
        int fundId, string? range, DateOnly? from, DateOnly? to, HttpContext http,
        MetaCache metaCache, IPortfolioEntitlements entitlements, FundRepository funds, PositionsCache cache, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var rangeText = (range ?? "YTD").ToUpperInvariant(); // README §6 P2: YTD when not given
        var kind = PerformanceRange.Parse(rangeText);
        if (kind is null)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown range", detail: "Use QTD, YTD, 1Y, ITD or CUSTOM.");
        if (kind == RangeKind.Custom && (from is null || to is null || from > to))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid custom range", detail: "CUSTOM needs from <= to (yyyy-MM-dd).");

        // A fund is visible when the user is entitled to one of its portfolios; otherwise it doesn't exist (404).
        var meta = await metaCache.GetAsync(ct);
        var allowed = entitlements.For(http.User, meta);
        if (!meta.Portfolios.Any(p => p.FundId == fundId && allowed.Contains(p.PortfolioId)))
            return NotFound(fundId);

        var label = kind == RangeKind.Custom ? $"CUSTOM:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}" : rangeText;
        var etag = $"W/\"fund:{fundId}:{meta.DataVersion}:{label}\"";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "private, no-cache";
        var audit = http.Features.Get<AuditFeature>();
        if (http.Request.Headers.IfNoneMatch.Contains(etag))
        {
            PositionsEndpoints.SetTiming(http, "HIT", 0, 0, started);
            if (audit is not null) audit.Cache = "HIT";
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var key = $"fund:{etag}";
        if (cache.Cache.TryGetValue(key, out byte[]? cached) && cached is not null)
        {
            PositionsEndpoints.SetTiming(http, "HIT", 0, 0, started);
            if (audit is not null) audit.Cache = "HIT";
            return Results.Bytes(cached, "application/json");
        }

        var dbStarted = Stopwatch.GetTimestamp();
        if (await funds.SpanAsync(fundId, ct) is not { } span)
            return NotFound(fundId);
        var (start, end) = PerformanceRange.Resolve(kind.Value, span.First, span.Last, from, to);
        // A range outside the fund's data (e.g. CUSTOM before inception) is empty arrays, not an error.
        var months = start <= end ? await funds.MonthsAsync(fundId, start, end, ct) : [];
        var dbMs = Stopwatch.GetElapsedTime(dbStarted).TotalMilliseconds;

        var serStarted = Stopwatch.GetTimestamp();
        var result = FundPivot.Pivot(span, label.Split(':')[0], months.Count > 0 ? start : null, months.Count > 0 ? end : null, months);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, DeskJsonContext.Default.FundPerformance);
        cache.Cache.Set(key, bytes, new MemoryCacheEntryOptions { Size = bytes.Length, AbsoluteExpiration = meta.BatchEndsAt });
        PositionsEndpoints.SetTiming(http, "MISS", dbMs, Stopwatch.GetElapsedTime(serStarted).TotalMilliseconds, started);
        if (audit is not null)
        {
            audit.Cache = "MISS";
            audit.Rows = months.Count;
        }
        return Results.Bytes(bytes, "application/json");
    }

    private static IResult NotFound(int fundId) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such fund", detail: $"Fund {fundId} is not available.");
}
