using Desk.Data.App;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Audit;

/// <summary>
/// Checks every <c>AUDIT_PURGE_CHECK_MINUTES</c> (default 60) whether the <see cref="AuditRetention"/> purge is due
/// and runs it, so rows past the window drain while nobody writes (#193, ADR-0022). The check itself is a clock
/// read: the database is touched only when a purge is due, at most once per <see cref="AuditRetention.PurgeInterval"/>
/// (sooner only to retry a failure), and not at start, so a cold start never wakes Neon. Each batch is its own
/// statement and commit. A failure is logged and retried at the next check; nothing here stops the host.
/// </summary>
public sealed class AuditPurgeTimer : BackgroundService
{
    public const string CheckConfigKey = "AUDIT_PURGE_CHECK_MINUTES";
    public const int DefaultCheckMinutes = 60;
    public const int MaxCheckMinutes = 1_440;

    private readonly IServiceProvider _services;
    private readonly AuditRetention _retention;
    private readonly ILogger<AuditPurgeTimer> _logger;
    // Created with the service, not in ExecuteAsync, which .NET 10 starts on a background thread: a clock tick
    // can't slip in before the timer exists.
    private readonly PeriodicTimer _timer;

    public AuditPurgeTimer(IServiceProvider services, IConfiguration config, AuditRetention retention, TimeProvider time, ILogger<AuditPurgeTimer> logger)
    {
        (_services, _retention, _logger) = (services, retention, logger);
        CheckEvery = TimeSpan.FromMinutes(CheckMinutes(config, logger));
        _timer = new PeriodicTimer(CheckEvery, time);
    }

    internal TimeSpan CheckEvery { get; }

    // A typo or 0 must not turn the timer off or into a tight loop: anything that isn't a whole number of minutes
    // from 1 to a day keeps the default (the LimitsOptions rule), with a warning.
    private static int CheckMinutes(IConfiguration config, ILogger logger)
    {
        var value = config[CheckConfigKey];
        if (int.TryParse(value, out var m) && m is >= 1 and <= MaxCheckMinutes) return m;
        if (!string.IsNullOrWhiteSpace(value))
            logger.LogWarning("{Key} must be a whole number of minutes from 1 to {Max}; keeping the default of {Default}.",
                CheckConfigKey, MaxCheckMinutes, DefaultCheckMinutes);
        return DefaultCheckMinutes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken))
                await CheckAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; batches that committed stay deleted, and the next start picks up the rest.
        }
    }

    public override void Dispose()
    {
        _timer.Dispose();
        base.Dispose();
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            // Creating a pooled context opens no connection: a check that isn't due never reaches the database.
            var contexts = _services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await contexts.CreateDbContextAsync(ct);
            if (await _retention.PurgeIfDueAsync(db, TimeSpan.MaxValue, releaseOnFailure: true, ct) is { } deleted)
                _logger.LogInformation("Purged {Count} audit entries older than {Days} days (timer).", deleted, _retention.Window.TotalDays);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Audit retention purge failed; retrying at the next check.");
        }
    }
}
