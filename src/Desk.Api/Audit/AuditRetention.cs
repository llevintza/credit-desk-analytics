using Desk.Data.App;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Audit;

/// <summary>
/// Retention for <c>app.audit</c> (ADR-0022, #114): rows older than <c>AUDIT_RETENTION_DAYS</c> (default 90) are
/// deleted at most once per <see cref="PurgeInterval"/>. <see cref="AuditWriter"/> runs the purge right after an
/// insert, so it only ever runs while the database is already awake: it never wakes Neon on its own.
/// </summary>
public sealed class AuditRetention
{
    public const string DaysConfigKey = "AUDIT_RETENTION_DAYS";
    public const int DefaultDays = 90;
    public const int MaxDays = 36_500;
    public static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);

    private readonly TimeProvider _time;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public AuditRetention(IConfiguration config, TimeProvider time, ILogger<AuditRetention> logger)
    {
        _time = time;
        var value = config[DaysConfigKey];
        var days = DefaultDays;
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (int.TryParse(value, out var d) && d is >= 1 and <= MaxDays)
                days = d;
            else
                logger.LogWarning("{Key} must be a whole number of days from 1 to {Max}; keeping the default of {Default}.",
                    DaysConfigKey, MaxDays, DefaultDays);
        }
        Window = TimeSpan.FromDays(days);
    }

    /// <summary>Rows with <c>at</c> older than now minus this window are purged.</summary>
    public TimeSpan Window { get; }

    /// <summary>True on the first write after start, then once per <see cref="PurgeInterval"/>.</summary>
    public bool IsDue => _time.GetUtcNow() - _lastAttempt >= PurgeInterval;

    /// <summary>
    /// Deletes rows older than the window: one <c>DELETE … WHERE at &lt; @cutoff</c> on <c>IX_audit_at</c>.
    /// The attempt is recorded first, so a failing purge is retried next interval, not on every write.
    /// </summary>
    public async Task<int> PurgeAsync(AppDbContext db, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        _lastAttempt = now;
        var cutoff = now - Window;
        return await db.Audit.Where(a => a.At < cutoff).ExecuteDeleteAsync(ct);
    }
}
