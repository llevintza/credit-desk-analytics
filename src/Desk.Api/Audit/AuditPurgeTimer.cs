using Desk.Data.App;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Audit;

/// <summary>
/// Checks every <c>AUDIT_PURGE_CHECK_MINUTES</c> (default 60) whether the <see cref="AuditRetention"/> purge is due
/// and runs it, so rows past the window drain while nobody writes (#193, ADR-0022). The check itself is a clock
/// read: the database is touched only when a purge is due, at most once per <see cref="AuditRetention.PurgeInterval"/>
/// (sooner only to retry a failure), and not at start, so a cold start never wakes Neon. Each batch is its own
/// statement and commit. A failure is logged and retried at the next check, at most <see cref="MaxQuickRetries"/>
/// times in a row, then the next day, so a purge that keeps failing can't wake the database every check. Nothing
/// here stops the host.
/// </summary>
public sealed class AuditPurgeTimer : BackgroundService
{
    public const string CheckConfigKey = "AUDIT_PURGE_CHECK_MINUTES";
    public const int DefaultCheckMinutes = 60;
    public const int MaxCheckMinutes = 1_440;
    /// <summary>Failed purges retried at the next check before the timer waits out the 24 h slot instead.</summary>
    internal const int MaxQuickRetries = 2;

    private readonly IServiceProvider _services;
    private readonly AuditRetention _retention;
    private readonly ILogger<AuditPurgeTimer> _logger;
    // Created with the service, not in ExecuteAsync, which .NET 10 starts on a background thread: a clock tick
    // can't slip in before the timer exists.
    private readonly PeriodicTimer _timer;
    private int _failures; // in a row; only the timer loop touches it
    private int _checks;

    public AuditPurgeTimer(IServiceProvider services, IConfiguration config, AuditRetention retention, TimeProvider time, ILogger<AuditPurgeTimer> logger)
    {
        (_services, _retention, _logger) = (services, retention, logger);
        CheckEvery = TimeSpan.FromMinutes(CheckMinutes(config, logger));
        _timer = new PeriodicTimer(CheckEvery, time);
    }

    internal TimeSpan CheckEvery { get; }

    /// <summary>Checks finished since start-up, whatever their outcome (diagnostics and tests).</summary>
    internal int Checks => Volatile.Read(ref _checks);

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
            if (await _retention.PurgeIfDueAsync(db, TimeSpan.MaxValue, releaseOnFailure: _failures < MaxQuickRetries, ct) is { } deleted)
            {
                _failures = 0;
                _logger.LogInformation("Purged {Count} audit entries older than {Days} days (timer).", deleted, _retention.Window.TotalDays);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _failures++;
            _logger.LogWarning(ex, "Audit retention purge failed ({Failures} in a row); retrying at the next check, or after 24 h once {Max} quick retries are used.",
                _failures, MaxQuickRetries);
        }
        finally
        {
            Interlocked.Increment(ref _checks);
        }
    }
}
