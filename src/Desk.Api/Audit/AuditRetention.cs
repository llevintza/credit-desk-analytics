using Desk.Data.App;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Audit;

/// <summary>
/// Retention for <c>app.audit</c> (ADR-0022, #114): rows older than <c>AUDIT_RETENTION_DAYS</c> (default 90) are
/// deleted at most once per <see cref="PurgeInterval"/>. <see cref="AuditWriter"/> runs the purge right after an
/// insert, while the database is already awake; <see cref="AuditPurgeTimer"/> checks on a timer too, so rows age
/// out while nobody writes (#193). Whichever comes first purges; the other finds the purge not due.
/// Safe to call concurrently: the due check and the claim of the slot are one compare-and-swap, so two callers
/// can't both purge in the same interval.
/// </summary>
public sealed class AuditRetention
{
    public const string DaysConfigKey = "AUDIT_RETENTION_DAYS";
    public const int DefaultDays = 90;
    public const int MaxDays = 36_500;
    public static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);
    /// <summary>Rows per DELETE: each batch commits on its own and stays far under the 10 s command timeout cold.</summary>
    public const int DefaultBatchSize = 50_000;
    /// <summary>Batches stop after this long; the rest is picked up by the next audit write, not 24 h later.</summary>
    public static readonly TimeSpan PurgeBudget = TimeSpan.FromSeconds(5);

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

    internal int BatchSize { get; init; } = DefaultBatchSize;

    /// <summary>
    /// When a purge is due (the first call after start, then once per <see cref="PurgeInterval"/>), deletes rows
    /// older than the window and returns how many; otherwise returns <c>null</c> without touching the database.
    /// Deletes run in batches of about <see cref="BatchSize"/> rows, oldest first, each its own statement and
    /// commit, until the backlog is gone or <see cref="PurgeBudget"/> runs out. Out of budget, the slot is
    /// released, so the next audit write carries on with the backlog. The slot is claimed before the first delete,
    /// so a failing purge is retried next interval, not on every write; batches that committed stay deleted.
    /// A batch is a time range on <c>IX_audit_at</c>: the <c>at</c> of the BatchSize-th oldest row (an index scan),
    /// then <c>DELETE … WHERE at &lt;= edge</c>. An <c>id IN (SELECT … LIMIT n)</c> delete would hash-join a scan of
    /// the whole table for every batch (ADR-0022). Ties at the edge make a batch a few rows larger, never smaller.
    /// </summary>
    public Task<int?> PurgeIfDueAsync(AppDbContext db, CancellationToken ct) =>
        PurgeIfDueAsync(db, PurgeBudget, releaseOnFailure: false, ct);

    /// <summary>
    /// The purge with the caller's time <paramref name="budget"/>. <see cref="AuditPurgeTimer"/> passes
    /// <see cref="TimeSpan.MaxValue"/> (it holds up no write, so it drains the whole backlog, still one batch per
    /// statement and commit) and <paramref name="releaseOnFailure"/>, so a failed purge is retried at its next check,
    /// not a day later.
    /// </summary>
    internal async Task<int?> PurgeIfDueAsync(AppDbContext db, TimeSpan budget, bool releaseOnFailure, CancellationToken ct)
    {
        if (!TryClaim(out var now, out var previous)) return null;
        var cutoff = now - Window;
        var total = 0;
        DateTimeOffset? edge;
        try
        {
            do
            {
                edge = await db.Audit.Where(a => a.At < cutoff).OrderBy(a => a.At).Skip(BatchSize - 1)
                    .Select(a => (DateTimeOffset?)a.At).FirstOrDefaultAsync(ct);
                total += edge is { } e
                    ? await db.Audit.Where(a => a.At <= e).ExecuteDeleteAsync(ct)
                    : await db.Audit.Where(a => a.At < cutoff).ExecuteDeleteAsync(ct); // the last, short batch
            }
            while (edge is not null && _time.GetUtcNow() - now < budget);
        }
        catch when (releaseOnFailure)
        {
            Release(now, previous);
            throw;
        }

        if (edge is not null) // out of budget, maybe with rows left: let the next write continue
            Release(now, previous);
        return total;
    }

    private void Release(DateTimeOffset claimed, long previous) =>
        Interlocked.CompareExchange(ref _lastAttemptTicks, previous, claimed.UtcTicks);

    private bool TryClaim(out DateTimeOffset now, out long previous)
    {
        now = _time.GetUtcNow();
        previous = Interlocked.Read(ref _lastAttemptTicks);
        return now.UtcTicks - previous >= PurgeInterval.Ticks
            && Interlocked.CompareExchange(ref _lastAttemptTicks, now.UtcTicks, previous) == previous;
    }
}
