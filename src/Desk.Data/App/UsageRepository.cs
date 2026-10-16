using Microsoft.EntityFrameworkCore;

namespace Desk.Data.App;

/// <summary>One user's audited API requests on one UTC day (README §7.2).</summary>
public sealed record UserDayUsage(DateOnly Day, string? UserName, int Requests, int Hits, int Misses, int Errors, int AvgMs);

/// <summary>All requests on one UTC day, with that day's cache hit ratio (<c>null</c> when nothing cacheable ran).</summary>
public sealed record DayUsage(DateOnly Day, int Requests, int Hits, int Misses, double? HitRatio);

/// <summary>One of the slowest audited requests of its UTC day.</summary>
public sealed record SlowRequest(DateOnly Day, DateTimeOffset At, string? UserName, string Endpoint, int Status, int? Rows, int Ms, string? Cache);

/// <summary>
/// The admin Usage page (README §7.2, §8, #70) for the UTC days <see cref="From"/> to <see cref="To"/>, both included.
/// <see cref="HitRatio"/> is hits / (hits + misses) over requests that carried a cache status; <c>null</c>, never
/// <c>NaN</c>, when there were none.
/// </summary>
public sealed record UsageReport(
    DateOnly From, DateOnly To, int Requests, int Hits, int Misses, double? HitRatio,
    IReadOnlyList<DayUsage> Days, IReadOnlyList<UserDayUsage> Users, IReadOnlyList<SlowRequest> Slowest)
{
    public static double? Ratio(int hits, int misses) => hits + misses == 0 ? null : (double)hits / (hits + misses);
}

/// <summary>
/// Reads the admin usage report from <c>app.audit</c> (ADR-0027): one GROUP BY over the window for requests per user
/// per day, and a LATERAL top-N per day for the slowest requests, both on <c>IX_audit_at</c>. Only
/// <see cref="AuditKinds.Request"/> rows count; login attempts are not queries. Days are UTC calendar days. Every value
/// is a parameter.
/// </summary>
public sealed class UsageRepository(IDbContextFactory<AppDbContext> contexts)
{
    public const int SlowestPerDay = 20;

    public async Task<UsageReport> ReadAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var lastDay = end.AddDays(-1);
        await using var db = await contexts.CreateDbContextAsync(ct);

        var users = await db.Database.SqlQuery<UserDayUsage>($"""
            SELECT (at AT TIME ZONE 'UTC')::date AS "Day", user_name AS "UserName",
                   count(*)::int AS "Requests",
                   (count(*) FILTER (WHERE cache = 'HIT'))::int AS "Hits",
                   (count(*) FILTER (WHERE cache = 'MISS'))::int AS "Misses",
                   (count(*) FILTER (WHERE status >= 500))::int AS "Errors",
                   round(avg(ms))::int AS "AvgMs"
            FROM app.audit
            WHERE at >= {start} AND at < {end} AND kind = {AuditKinds.Request}
            GROUP BY 1, 2
            ORDER BY 1 DESC, 3 DESC, 2
            """).ToListAsync(ct);

        // A top-N heap per day (bounded memory) instead of ranking the whole window with ROW_NUMBER, which sorts every
        // row of a 30-day window and spills it to disk (ADR-0027).
        var slowest = await db.Database.SqlQuery<SlowRequest>($"""
            SELECT (d AT TIME ZONE 'UTC')::date AS "Day", s.at AS "At", s.user_name AS "UserName", s.endpoint AS "Endpoint",
                   s.status AS "Status", s.rows AS "Rows", s.ms AS "Ms", s.cache AS "Cache"
            FROM generate_series({start}, {lastDay}, interval '1 day') AS d
            CROSS JOIN LATERAL (
                SELECT at, user_name, endpoint, status, rows, ms, cache, id
                FROM app.audit
                WHERE at >= d AND at < d + interval '1 day' AND kind = {AuditKinds.Request}
                ORDER BY ms DESC, id
                LIMIT {SlowestPerDay}) s
            ORDER BY d DESC, s.ms DESC, s.id
            """).ToListAsync(ct);

        var days = users.GroupBy(u => u.Day)
            .Select(g => (Day: g.Key, Requests: g.Sum(u => u.Requests), Hits: g.Sum(u => u.Hits), Misses: g.Sum(u => u.Misses)))
            .Select(d => new DayUsage(d.Day, d.Requests, d.Hits, d.Misses, UsageReport.Ratio(d.Hits, d.Misses)))
            .OrderByDescending(d => d.Day)
            .ToList();
        int hits = days.Sum(d => d.Hits), misses = days.Sum(d => d.Misses);
        return new UsageReport(from, to, days.Sum(d => d.Requests), hits, misses, UsageReport.Ratio(hits, misses), days, users, slowest);
    }
}
