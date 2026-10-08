using Desk.Api.Audit;
using Desk.Data.App;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Desk.Api.Tests;

/// <summary>
/// #114 / ADR-0022: audit rows are kept for <c>AUDIT_RETENTION_DAYS</c> (default 90) and purged on a schedule.
/// Each test marks its rows with its own endpoint so it never counts or depends on another test's rows.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditRetentionTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> InsertAsync(params DateTimeOffset[] at)
    {
        var marker = $"GET /retention-test/{Guid.NewGuid():N}";
        await using var db = api.NewContext();
        db.Audit.AddRange(at.Select(a => new AuditEntry { At = a, Kind = AuditKinds.Request, Endpoint = marker, Status = 200 }));
        await db.SaveChangesAsync(Ct);
        return marker;
    }

    private async Task<List<DateTimeOffset>> RemainingAsync(string marker)
    {
        await using var db = api.NewContext();
        return await db.Audit.AsNoTracking().Where(a => a.Endpoint == marker).OrderBy(a => a.At).Select(a => a.At).ToListAsync(Ct);
    }

    [Fact]
    public async Task The_purge_deletes_rows_older_than_90_days_and_keeps_the_rest()
    {
        // Own clock, a year ahead of the shared one: other tests move the shared clock, and the shared host's own
        // 90-day purge must not reach these rows.
        var now = api.Time.GetUtcNow().AddDays(365);
        var retention = AuditTests.Retention(time: new FakeTimeProvider(now));
        var window = TimeSpan.FromDays(90);
        var marker = await InsertAsync(
            now - window - TimeSpan.FromDays(30),
            now - window - TimeSpan.FromSeconds(1),
            now - window,                          // exactly 90 days old: kept ("older than" the window)
            now - window + TimeSpan.FromSeconds(1),
            now);

        await using (var db = api.NewContext())
            Assert.True(await retention.PurgeAsync(db, Ct) >= 2);

        Assert.Equal([now - window, now - window + TimeSpan.FromSeconds(1), now], await RemainingAsync(marker));
    }

    [Fact]
    public async Task The_app_purges_after_an_audit_write_with_the_configured_window()
    {
        await using var host = api.WithSettings((AuditRetention.DaysConfigKey, "30"));
        var now = api.Time.GetUtcNow();
        var marker = await InsertAsync(now - TimeSpan.FromDays(32), now - TimeSpan.FromDays(28));

        // A login writes an audit row; this host's writer then runs its first purge (30-day window).
        var user = await api.CreateUserAsync();
        await PostgresApiFactory.LoginAsync(PostgresApiFactory.NewClient(host), user.Email!);

        for (var i = 0; i < 100 && (await RemainingAsync(marker)).Count > 1; i++)
            await Task.Delay(100, Ct);
        var left = Assert.Single(await RemainingAsync(marker));
        Assert.Equal(now - TimeSpan.FromDays(28), left);
    }
}
