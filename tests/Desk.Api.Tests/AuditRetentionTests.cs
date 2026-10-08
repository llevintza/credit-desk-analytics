using Desk.Api.Audit;
using Desk.Data.App;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>
/// #114 / ADR-0022: audit rows are kept for <c>AUDIT_RETENTION_DAYS</c> (default 90) and purged on a schedule.
/// The purge is table-wide, so direct purge tests run on their own migrated database in the same container.
/// The end-to-end test purges the shared <c>app.audit</c> with a 30-day window on the shared clock: only its own
/// rows are that old, and its assertions read only its own marked rows.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditRetentionTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A migrated, empty database in the shared container, so a table-wide purge touches no other test's rows.</summary>
    private async Task<string> IsolatedDatabaseAsync()
    {
        var cs = new NpgsqlConnectionStringBuilder(api.ConnectionString) { Database = $"audit_{Guid.NewGuid():N}" }.ConnectionString;
        await using (var conn = new NpgsqlConnection(api.ConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{new NpgsqlConnectionStringBuilder(cs).Database}\"", conn);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        await using var db = Context(cs);
        await db.Database.MigrateAsync(Ct);
        return cs;
    }

    /// <summary>A context on <paramref name="cs"/>, or on the shared database when it is null.</summary>
    private AppDbContext Context(string? cs = null) => cs is null ? api.NewContext() : new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(cs, n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)).Options);

    private async Task<string> InsertAsync(string? cs, params DateTimeOffset[] at)
    {
        var marker = $"GET /retention-test/{Guid.NewGuid():N}";
        await using var db = Context(cs);
        db.Audit.AddRange(at.Select(a => new AuditEntry { At = a, Kind = AuditKinds.Request, Endpoint = marker, Status = 200 }));
        await db.SaveChangesAsync(Ct);
        return marker;
    }

    private async Task<List<DateTimeOffset>> RemainingAsync(string marker, string? cs = null)
    {
        await using var db = Context(cs);
        return await db.Audit.AsNoTracking().Where(a => a.Endpoint == marker).OrderBy(a => a.At).Select(a => a.At).ToListAsync(Ct);
    }

    [Fact]
    public async Task The_purge_deletes_rows_older_than_90_days_and_keeps_the_rest()
    {
        var cs = await IsolatedDatabaseAsync();
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var retention = AuditTests.Retention(time: new FakeTimeProvider(now));
        var window = TimeSpan.FromDays(90);
        var marker = await InsertAsync(cs,
            now - window - TimeSpan.FromDays(30),
            now - window - TimeSpan.FromSeconds(1),
            now - window,                          // exactly 90 days old: kept ("older than" the window)
            now - window + TimeSpan.FromSeconds(1),
            now);

        await using (var db = Context(cs))
            Assert.Equal(2, await retention.PurgeIfDueAsync(db, Ct));

        Assert.Equal([now - window, now - window + TimeSpan.FromSeconds(1), now], await RemainingAsync(marker, cs));
    }

    [Fact]
    public async Task A_backlog_is_deleted_in_batches_oldest_first()
    {
        var cs = await IsolatedDatabaseAsync();
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var retention = new AuditRetention(new ConfigurationBuilder().Build(), new FakeTimeProvider(now), NullLogger<AuditRetention>.Instance) { BatchSize = 2 };
        var marker = await InsertAsync(cs, [.. Enumerable.Range(91, 5).Select(d => now - TimeSpan.FromDays(d)), now]);

        await using (var db = Context(cs))
            Assert.Equal(5, await retention.PurgeIfDueAsync(db, Ct)); // 2 + 2 + 1, within the budget

        Assert.Equal([now], await RemainingAsync(marker, cs));
    }

    [Fact]
    public async Task Rows_tied_at_a_batch_edge_go_in_the_same_batch()
    {
        var cs = await IsolatedDatabaseAsync();
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var retention = new AuditRetention(new ConfigurationBuilder().Build(), new FakeTimeProvider(now), NullLogger<AuditRetention>.Instance) { BatchSize = 2 };
        var tied = now - TimeSpan.FromDays(95);
        var marker = await InsertAsync(cs, now - TimeSpan.FromDays(99), tied, tied, tied, now - TimeSpan.FromDays(89));

        await using (var db = Context(cs))
            Assert.Equal(4, await retention.PurgeIfDueAsync(db, Ct)); // the edge is the tied time: all three go

        Assert.Equal([now - TimeSpan.FromDays(89)], await RemainingAsync(marker, cs));
    }

    [Fact]
    public async Task Out_of_budget_the_next_write_carries_on_with_the_backlog()
    {
        var cs = await IsolatedDatabaseAsync();
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        // Every clock read moves 6 s, so the 5 s budget runs out after each first batch.
        var time = new FakeTimeProvider(now) { AutoAdvanceAmount = TimeSpan.FromSeconds(6) };
        var retention = new AuditRetention(new ConfigurationBuilder().Build(), time, NullLogger<AuditRetention>.Instance) { BatchSize = 2 };
        var marker = await InsertAsync(cs, [.. Enumerable.Range(91, 5).Select(d => now - TimeSpan.FromDays(d))]);

        await using var db = Context(cs);
        Assert.Equal(2, await retention.PurgeIfDueAsync(db, Ct)); // out of budget: slot released
        Assert.Equal(2, await retention.PurgeIfDueAsync(db, Ct)); // right away, not 24 h later
        Assert.Equal(1, await retention.PurgeIfDueAsync(db, Ct)); // short batch: done, slot kept
        Assert.Null(await retention.PurgeIfDueAsync(db, Ct));
        Assert.Empty(await RemainingAsync(marker, cs));
    }

    [Fact]
    public async Task The_app_purges_after_an_audit_write_with_the_configured_window()
    {
        await using var host = api.WithSettings((AuditRetention.DaysConfigKey, "30"));
        var now = api.Time.GetUtcNow();
        var marker = await InsertAsync(null, now - TimeSpan.FromDays(32), now - TimeSpan.FromDays(28));

        // A login writes an audit row; this host's writer then runs its first purge (30-day window).
        var user = await api.CreateUserAsync();
        await PostgresApiFactory.LoginAsync(PostgresApiFactory.NewClient(host), user.Email!);

        for (var i = 0; i < 100 && (await RemainingAsync(marker)).Count > 1; i++)
            await Task.Delay(100, Ct);
        var left = Assert.Single(await RemainingAsync(marker));
        Assert.Equal(now - TimeSpan.FromDays(28), left);
    }
}
