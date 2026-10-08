using System.Diagnostics;
using Desk.Api.Auth;
using Desk.Api.Limits;
using Desk.Data.App;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app, RouteGroupBuilder api)
    {
        // Static on purpose: platform probes must never wake the database (README §7.2).
        // `version` is the git SHA baked in at image build time; the deploy pipeline waits for it (README §14.2).
        // `maintenance` lets the login page show its banner while every /api call returns 503.
        var version = app.Configuration["APP_VERSION"] ?? "dev";
        app.MapGet("/health", (IConfiguration config) => Results.Ok(new HealthResponse("ok", version, MaintenanceMode.IsOn(config))))
           .WithName("Health").WithTags("Health")
           .WithSummary("Liveness probe with the deployed build version; never touches the database.");

        // Under /api so maintenance mode, the rate limits and the admin policy all apply.
        api.MapGet("/health/db", CheckDatabaseAsync)
           .RequireAuthorization(AuthSetup.AdminPolicy)
           .WithName("HealthDb").WithTags("Health")
           .WithSummary("Admin-only database check: opens a connection and runs SELECT 1.")
           .Produces<DbHealthResponse>()
           .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    internal static async Task<IResult> CheckDatabaseAsync(IDbContextFactory<AppDbContext> contexts, ILogger<AppDbContext> logger, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            return Results.Ok(new DbHealthResponse("ok", (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception can carry the host name; it goes to the log, never to the client.
            logger.LogWarning(ex, "Database health check failed.");
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
        }
    }
}

public sealed record HealthResponse(string Status, string Version, bool Maintenance);

public sealed record DbHealthResponse(string Status, int Ms);
