using System.Diagnostics;
using System.Text.Json;
using Desk.Api.Audit;
using Desk.Api.Auth;
using Desk.Api.Positions;
using Desk.Data.App;

namespace Desk.Api.Admin;

public static class AdminEndpoints
{
    /// <summary>Default and largest Usage window, in UTC days ending today (ADR-0027: 30 days stay near 200 ms on a busy table).</summary>
    public const int DefaultUsageDays = 7;
    public const int MaxUsageDays = 31;

    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Admin").RequireAuthorization(AuthSetup.AdminPolicy);
        admin.MapGet("/usage", UsageAsync)
            .WithName("GetUsage")
            .WithSummary("Requests per user per day, cache hit ratio and the slowest 20 requests per day, from the audit log "
                + $"(the last `days` UTC days, {DefaultUsageDays} by default, at most {MaxUsageDays} and never past the audit retention window).")
            .Produces<UsageReport>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        admin.MapPost("/cache/clear", ClearCache)
            .WithName("ClearCache")
            .WithSummary("Drops cached reference data and grid blocks (after a manual reseed).")
            .Produces(StatusCodes.Status204NoContent);
        return api;
    }

    /// <summary>
    /// README §7.2 / §8 Usage (#70): read live from <c>app.audit</c>, never cached, so the page shows requests up to the
    /// last audit flush. The window is clamped to <see cref="AuditRetention.Window"/>: older rows are gone anyway.
    /// The request's own audit row carries no cache status, so reading the page doesn't move the hit ratio.
    /// </summary>
    internal static async Task<IResult> UsageAsync(
        int? days, HttpContext http, UsageRepository usage, AuditRetention retention, TimeProvider time, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var window = days ?? DefaultUsageDays;
        if (window is < 1 or > MaxUsageDays)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid days",
                detail: $"days must be a whole number from 1 to {MaxUsageDays}.");
        var (from, to) = UsageWindow(window, retention.Window, time.GetUtcNow());
        var report = await usage.ReadAsync(from, to, ct);
        var dbMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var serStarted = Stopwatch.GetTimestamp();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, DeskJsonContext.Default.UsageReport);
        http.Response.Headers.CacheControl = "no-store"; // live, and it names users
        PositionsEndpoints.SetTiming(http, "MISS", dbMs, Stopwatch.GetElapsedTime(serStarted).TotalMilliseconds, started);
        if (http.Features.Get<AuditFeature>() is { } audit) audit.Rows = report.Users.Count + report.Slowest.Count;
        return Results.Bytes(bytes, "application/json");
    }

    /// <summary>
    /// The last <paramref name="days"/> UTC days ending today, at most <paramref name="retention"/> of them. Every day
    /// shown is whole (#70): a purge at any time t up to <paramref name="now"/> deletes only rows older than
    /// t - retention, which falls on or before the day before the oldest one shown.
    /// </summary>
    internal static (DateOnly From, DateOnly To) UsageWindow(int days, TimeSpan retention, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return (today.AddDays(1 - Math.Min(days, (int)retention.TotalDays)), today);
    }

    /// <summary>README §8: after a db-ops reseed the next request reloads the catalog, dates and data version.</summary>
    internal static IResult ClearCache(MetaCache meta, PositionsCache positions, ILogger<MetaCache> logger, HttpContext http)
    {
        meta.Invalidate();
        positions.Clear();
        logger.LogInformation("Caches cleared by {User}.", http.User.Identity!.Name);
        return Results.NoContent();
    }
}
