using Desk.Data.Insights;
using Desk.Data.Sources;

namespace Desk.Api.Limits;

/// <summary>
/// README §7.2 (#211): the shared database permits are the intended limiter, so they must fit the Npgsql pool
/// (<c>DB_MAX_POOL_SIZE</c>). If they don't, the pool limits first: requests wait out Npgsql's pool timeout and fail
/// with a pool-exhausted error instead of a 429 with <c>Retry-After</c>.
/// </summary>
public static class ConnectionBudget
{
    /// <summary>
    /// Connections used outside the request permits: the single-flight meta load, the audit writer and purge,
    /// DataProtection key reads and the <c>/health/db</c> probe.
    /// </summary>
    public const int Headroom = 5;

    /// <summary>
    /// The most connections requests can hold at once. Each permit holds one connection, except an insights read,
    /// which holds up to <see cref="InsightsOptions.PerRequest"/>, all of them together at most
    /// <see cref="InsightsOptions.Global"/>; the worst case is the split of permits between the two that holds most.
    /// </summary>
    public static int Peak(LimitsOptions limits, InsightsOptions insights) =>
        Enumerable.Range(0, limits.GlobalConcurrency + 1)
            .Max(k => limits.GlobalConcurrency - k + Math.Min(insights.Global, k * insights.PerRequest));

    public static int Needed(LimitsOptions limits, InsightsOptions insights) => Peak(limits, insights) + Headroom;

    /// <summary>Why the settings don't fit the pool, naming each setting (never a connection string), or null.</summary>
    public static string? Problem(LimitsOptions limits, InsightsOptions insights, int maxPoolSize)
    {
        var needed = Needed(limits, insights);
        return needed <= maxPoolSize
            ? null
            : $"RATE_LIMIT_GLOBAL_CONCURRENCY ({limits.GlobalConcurrency}) with INSIGHTS_MAX_CONNECTIONS ({insights.Global}) and " +
              $"INSIGHTS_PARALLELISM ({insights.PerRequest}) can hold {Peak(limits, insights)} connections, plus {Headroom} for background " +
              $"work: {needed}, above DB_MAX_POOL_SIZE ({maxPoolSize}). Under load the pool, not the limiter, will refuse requests " +
              "(a timeout instead of a 429). Raise DB_MAX_POOL_SIZE or lower the others (README §7.2).";
    }
}

/// <summary>Logs <see cref="ConnectionBudget.Problem"/> once at start-up. A warning, not a refusal to start: the app still serves.</summary>
public sealed class ConnectionBudgetCheck(LimitsOptions limits, InsightsOptions insights, IConfiguration config, ILogger<ConnectionBudgetCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (ConnectionBudget.Problem(limits, insights, DataSourceRegistry.MaxPoolSizeFrom(config)) is { } problem)
            logger.LogWarning("{Problem}", problem);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
