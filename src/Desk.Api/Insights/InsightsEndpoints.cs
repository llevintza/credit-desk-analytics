using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Desk.Api.Positions;
using Desk.Data.Insights;

namespace Desk.Api.Insights;

/// <summary>
/// The dev-only fault knob for the P3 progressive-render e2e (README §6 P3 acceptance): <c>X-Debug-Delay-Ms</c> holds a
/// response back. Honoured only in Development or with <c>DEV_FAULT_INJECTION=true</c> (the e2e compose override);
/// production never sets it, so the header is ignored there.
/// </summary>
public sealed record InsightsFaults(bool Enabled)
{
    public const string DelayHeader = "X-Debug-Delay-Ms";
    public const int MaxDelayMs = 5000;

    public static InsightsFaults From(IConfiguration config, IHostEnvironment env) =>
        new(env.IsDevelopment() || string.Equals(config["DEV_FAULT_INJECTION"], "true", StringComparison.Ordinal));

    /// <summary>The requested delay, clamped to 0–<see cref="MaxDelayMs"/>; zero when disabled or absent.</summary>
    public TimeSpan DelayFor(HttpRequest request) =>
        Enabled && int.TryParse(request.Headers[DelayHeader], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            ? TimeSpan.FromMilliseconds(Math.Min(ms, MaxDelayMs))
            : TimeSpan.Zero;
}

/// <summary>P3 Insights Board (README §6): one endpoint per data source, its grids read in parallel, cache-first.</summary>
public static class InsightsEndpoints
{
    public static RouteGroupBuilder MapInsightsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/insights/{source}", InsightsAsync)
            .WithTags("Insights")
            .WithName("GetInsights")
            .WithSummary("One data source's small grids (core | market | surveillance | pricing | reference) for an as-of date and the "
                + "caller's portfolios; grid=<id> returns just that grid (naive mode).")
            .Produces<InsightsResult>()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    internal static async Task<IResult> InsightsAsync(
        string source, DateOnly? asOf, string? portfolioIds, string? grid, HttpContext http,
        MetaCache metaCache, IPortfolioEntitlements entitlements, InsightsRepository insights, PositionsCache cache,
        InsightsFaults faults, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        source = source.ToLowerInvariant();
        if (!InsightCatalog.Sources.Contains(source))
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown source",
                detail: $"Use one of: {string.Join(", ", InsightCatalog.Sources)}.");
        IReadOnlyList<InsightSpec> specs = InsightCatalog.For(source);
        if (grid is not null)
        {
            if (InsightCatalog.Find(source, grid) is not { } one)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown grid",
                    detail: $"Source '{source}' has no grid '{grid}'.");
            specs = [one];
        }
        if (!TryParseIds(portfolioIds, out var requested))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid portfolioIds",
                detail: "portfolioIds is a comma-separated list of integers.");

        var delay = faults.DelayFor(http.Request);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);

        var meta = await metaCache.GetAsync(ct);
        if (!meta.HasData)
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No data loaded",
                detail: "The database has not been seeded yet.");
        var date = asOf ?? meta.AsOfDates[0];
        if (!meta.AsOfDates.Contains(date))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown as-of date",
                detail: $"No data for {date:yyyy-MM-dd}. See GET /api/meta/as-of.");

        // Entitlements first (ADR-0021): requested ids outside the caller's set are dropped; none requested = all of it.
        var entitled = entitlements.For(http.User, meta);
        int[] portfolios = [.. (requested.Length > 0 ? requested.Where(entitled.Contains) : entitled).Distinct().Order()];

        var key = $"{source}:{grid ?? "*"}:{date:yyyy-MM-dd}:{meta.DataVersion}:{string.Join(',', portfolios)}";
        var etag = $"W/\"ins:{PositionsEndpoints.Hash(key)}\"";
        if (CachedResponse.TryHit(http, cache, etag, "application/json", started) is { } hit)
            return hit;

        var dbStarted = Stopwatch.GetTimestamp();
        var grids = await insights.ReadAsync(specs, date, portfolios, ct);
        var dbMs = Stopwatch.GetElapsedTime(dbStarted).TotalMilliseconds;

        var serStarted = Stopwatch.GetTimestamp();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new InsightsResult(source, date, grids), DeskJsonContext.Default.InsightsResult);
        return CachedResponse.Store(http, cache, etag, bytes, "application/json", meta.BatchEndsAt, dbMs,
            Stopwatch.GetElapsedTime(serStarted).TotalMilliseconds, started, grids.Sum(g => g.Rows.Length));
    }

    /// <summary>"3,7" → [3, 7]; null or blank → []. At most 100 ids (there are 12 portfolios).</summary>
    internal static bool TryParseIds(string? text, out int[] ids)
    {
        ids = [];
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 100) return false;
        var parsed = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
                return false;
        ids = parsed;
        return true;
    }
}
