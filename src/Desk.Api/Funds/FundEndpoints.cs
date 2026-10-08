using System.Diagnostics;
using System.Text.Json;
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

    /// <summary>A cached span lookup: <c>null</c> Span means "this fund has no performance data".</summary>
    private sealed record SpanEntry(FundSpan? Span);

    internal static async Task<IResult> PerformanceAsync(
        int fundId, string? range, DateOnly? from, DateOnly? to, HttpContext http,
        MetaCache metaCache, IPortfolioEntitlements entitlements, FundRepository funds, PositionsCache cache, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var rangeText = (range ?? "YTD").ToUpperInvariant(); // README §6 P2: YTD when not given
        var kind = PerformanceRange.Parse(rangeText);
        if (kind is null)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown range", detail: "Use QTD, YTD, 1Y, ITD or CUSTOM.");
        if (kind == RangeKind.Custom && (from is not { } f || to is not { } t || f > t))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid custom range", detail: "CUSTOM needs from <= to (yyyy-MM-dd).");

        // A fund is visible when the user is entitled to one of its portfolios; otherwise it doesn't exist (404).
        var meta = await metaCache.GetAsync(ct);
        var allowed = entitlements.For(http.User, meta);
        if (!meta.Portfolios.Any(p => p.FundId == fundId && allowed.Contains(p.PortfolioId)))
            return NotFound(fundId);

        // The fund's span (first/last month-end) changes only with a reseed: cached, including "no data", so
        // neither a hit nor a 404 needs the database.
        var dbStarted = Stopwatch.GetTimestamp();
        var spanKey = $"fundspan:{meta.DataVersion}:{fundId}";
        if (!cache.Cache.TryGetValue(spanKey, out SpanEntry? entry) || entry is null)
        {
            entry = new SpanEntry(await funds.SpanAsync(fundId, ct));
            cache.Cache.Set(spanKey, entry, new MemoryCacheEntryOptions { Size = 128, AbsoluteExpiration = meta.BatchEndsAt });
        }
        if (entry.Span is not { } span)
            return NotFound(fundId); // no ETag: there's nothing to revalidate

        var label = kind == RangeKind.Custom ? $"CUSTOM:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}" : rangeText;
        var etag = $"W/\"fund:{fundId}:{meta.DataVersion}:{label}\"";
        if (CachedResponse.TryHit(http, cache, etag, "application/json", started) is { } hit)
            return hit;

        var (start, end) = PerformanceRange.Resolve(kind.Value, span.First, span.Last, from, to);
        // A range outside the fund's data (e.g. CUSTOM before inception) is empty arrays, not an error.
        var months = start <= end ? await funds.MonthsAsync(fundId, start, end, ct) : [];
        var dbMs = Stopwatch.GetElapsedTime(dbStarted).TotalMilliseconds;

        var serStarted = Stopwatch.GetTimestamp();
        var result = FundPivot.Pivot(span, kind == RangeKind.Custom ? "CUSTOM" : rangeText, months.Count > 0 ? start : null, months.Count > 0 ? end : null, months);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, DeskJsonContext.Default.FundPerformance);
        return CachedResponse.Store(http, cache, etag, bytes, "application/json", meta.BatchEndsAt, dbMs,
            Stopwatch.GetElapsedTime(serStarted).TotalMilliseconds, started, months.Count);
    }

    private static IResult NotFound(int fundId) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such fund", detail: $"Fund {fundId} is not available.");
}
