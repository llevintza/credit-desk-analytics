using Desk.Data.App;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Audit;

/// <summary>
/// Retention for <c>app.audit</c> (ADR-0022, #114): rows older than <c>AUDIT_RETENTION_DAYS</c> (default 90) are
/// deleted at most once per <see cref="PurgeInterval"/>. <see cref="AuditWriter"/> runs the purge right after an
/// insert, so it only ever runs while the database is already awake: it never wakes Neon on its own.
/// Safe to call concurrently: the due check and the claim of the slot are one compare-and-swap, so two callers
/// can't both purge in the same interval.
/// </summary>
public sealed class AuditRetention
{
    public const string DaysConfigKey = "AUDIT_RETENTION_DAYS";
    public const int DefaultDays = 90;
    public const int MaxDays = 36_500;
    public static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);

    private readonly TimeProvider _time;
    private long _lastAttemptTicks = DateTimeOffset.MinValue.UtcTicks;

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

    /// <summary>
    /// When a purge is due (the first call after start, then once per <see cref="PurgeInterval"/>), deletes rows
    /// older than the window and returns how many; otherwise returns <c>null</c> without touching the database.
    /// The slot is claimed before the delete, so a failing purge is retried next interval, not on every write.
    /// </summary>
    public async Task<int?> PurgeIfDueAsync(AppDbContext db, CancellationToken ct)
    {
        if (!TryClaim(out var now)) return null;
        var cutoff = now - Window;
        return await db.Audit.Where(a => a.At < cutoff).ExecuteDeleteAsync(ct);
    }

    private bool TryClaim(out DateTimeOffset now)
    {
        now = _time.GetUtcNow();
        var last = Interlocked.Read(ref _lastAttemptTicks);
        return now.UtcTicks - last >= PurgeInterval.Ticks
            && Interlocked.CompareExchange(ref _lastAttemptTicks, now.UtcTicks, last) == last;
    }
}
