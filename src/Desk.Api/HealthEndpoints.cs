using Desk.Api.Limits;
using Desk.Data.App;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // Static on purpose: platform probes must never wake the database (README §7.2).
        // `version` is the git SHA baked in at image build time; the deploy pipeline waits for it (README §14.2).
        // `maintenance` lets the login page show its banner while every /api call returns 503.
        var version = app.Configuration["APP_VERSION"] ?? "dev";
        app.MapGet("/health", (IConfiguration config) => Results.Ok(new HealthResponse("ok", version, MaintenanceMode.IsOn(config))))
           .WithName("Health").WithTags("Health")
           .WithSummary("Liveness probe with the deployed build version; never touches the database.");

        // README §8 (#113): the same anonymous exposure as /health, outside /api (no session, no rate limit), and it
        // says up or down, nothing else. DbHealthProbe shares one check among all callers, so polling opens at most
        // one connection per interval. Platform probes stay on /health.
        app.MapGet("/health/db", CheckDatabaseAsync)
           .WithName("HealthDb").WithTags("Health")
           .WithSummary("Database probe: up or down only. Checks at most once per 10 s, and never in maintenance mode.")
           .Produces<DbHealthResponse>()
           .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    internal static async Task<IResult> CheckDatabaseAsync(DbHealthProbe probe, IConfiguration config, CancellationToken ct) =>
        // Maintenance mode keeps the database idle, so it isn't checked: down.
        !MaintenanceMode.IsOn(config) && await probe.IsUpAsync(ct)
            ? Results.Ok(new DbHealthResponse("up"))
            : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: DownTitle);

    /// <summary>The whole of a 503: a problem with this title and no detail (ADR-0019 keeps every error problem+json).</summary>
    public const string DownTitle = "Database down";
}

/// <summary>
/// One database check (open a connection, <c>SELECT 1</c>) shared by every <c>/health/db</c> caller for
/// <see cref="CacheFor"/> (#113). The endpoint is anonymous and outside the rate limits, so it must not open a
/// connection per request.
/// </summary>
public sealed class DbHealthProbe(IServiceProvider services, TimeProvider time, ILogger<DbHealthProbe> logger)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    /// <summary>The check's own deadline: it is shared, so no single caller's cancellation may end it.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private Task<bool>? _check;
    private long _startedAt;

    public Task<bool> IsUpAsync(CancellationToken ct)
    {
        Task<bool> check;
        lock (_lock)
        {
            if (_check is null || time.GetElapsedTime(_startedAt) >= CacheFor)
            {
                _startedAt = time.GetTimestamp();
                _check = CheckAsync();
            }
            check = _check;
        }
        return check.WaitAsync(ct);
    }

    private async Task<bool> CheckAsync()
    {
        using var deadline = new CancellationTokenSource(Deadline, time);
        try
        {
            // Resolved here, on first use as AddDeskData intends: with no database configured the factory can't be
            // built, and that is down, not a 500.
            var contexts = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await contexts.CreateDbContextAsync(deadline.Token);
            await db.Database.ExecuteSqlRawAsync("SELECT 1", deadline.Token);
            return true;
        }
        catch (Exception ex)
        {
            // The exception can carry the host name; it goes to the log, never to the client.
            logger.LogWarning(ex, "Database health check failed.");
            return false;
        }
    }
}

public sealed record HealthResponse(string Status, string Version, bool Maintenance);

/// <summary><c>up</c>, and nothing else (#113). Down is a 503 problem titled <see cref="HealthEndpoints.DownTitle"/>.</summary>
public sealed record DbHealthResponse(string Status);
