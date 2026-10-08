using Desk.Api.Audit;
using Desk.Data.App;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Desk.Api.Tests;

/// <summary>README §7.2: audit writes never block or fail the request path.</summary>
public sealed class AuditTests
{
    private static IConfiguration Flush(int? seconds) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [AuditWriter.FlushConfigKey] = seconds?.ToString() })
        .Build();

    internal static AuditRetention Retention(string? days = null, TimeProvider? time = null) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [AuditRetention.DaysConfigKey] = days }).Build(),
        time ?? TimeProvider.System, NullLogger<AuditRetention>.Instance);

    /// <summary>A pooled factory pointed at a closed port: every query fails fast.</summary>
    private static ServiceProvider DeadDatabase()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<AppDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null, 90)]
    [InlineData("", 90)]
    [InlineData("0", 90)]
    [InlineData("-7", 90)]
    [InlineData("ninety", 90)]
    [InlineData("36501", 90)]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    [InlineData("36500", 36500)]
    public void Retention_window_defaults_to_90_days(string? value, int expectedDays)
    {
        Assert.Equal(TimeSpan.FromDays(expectedDays), Retention(value).Window);
    }

    [Fact]
    public async Task A_failed_purge_is_retried_next_interval_not_on_every_write()
    {
        await using var sp = DeadDatabase();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        var retention = Retention(time: time);
        Assert.True(retention.IsDue); // the first write after start purges

        await using var db = await sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => retention.PurgeAsync(db, TestContext.Current.CancellationToken));
        Assert.False(retention.IsDue);

        time.Advance(AuditRetention.PurgeInterval - TimeSpan.FromSeconds(1));
        Assert.False(retention.IsDue);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(retention.IsDue);
    }

    [Fact]
    public async Task The_writer_logs_a_failed_or_cancelled_purge_instead_of_throwing()
    {
        await using var sp = DeadDatabase();
        var retention = Retention();
        var writer = new AuditWriter(new AuditQueue(), sp, Flush(0), retention, NullLogger<AuditWriter>.Instance);
        await using var db = await sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);

        await writer.PurgeAsync(db, TestContext.Current.CancellationToken); // connection refused: logged
        await writer.PurgeAsync(db, new CancellationToken(canceled: true)); // shutting down: quiet
        Assert.False(retention.IsDue);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("-1", 30)]
    [InlineData("soon", 30)]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    public void Flush_interval_defaults_to_30_seconds(string? value, int expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [AuditWriter.FlushConfigKey] = value }).Build();
        var writer = new AuditWriter(new AuditQueue(), new ServiceCollection().BuildServiceProvider(), config, Retention(), NullLogger<AuditWriter>.Instance);
        Assert.Equal(TimeSpan.FromSeconds(expected), writer.FlushInterval);
    }

    [Fact]
    public async Task A_full_batch_is_written_without_waiting_for_the_interval()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<AppDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
        await using var sp = services.BuildServiceProvider();
        var queue = new AuditQueue();
        for (var i = 0; i < AuditWriter.MaxBatch; i++) queue.Enqueue(Entry());
        var writer = new AuditWriter(queue, sp, Flush(3600), Retention(), NullLogger<AuditWriter>.Instance);

        await writer.StartAsync(TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (queue.Reader.Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(0, queue.Reader.Count); // drained long before the 1 h interval
        await writer.StopAsync(TestContext.Current.CancellationToken);
    }

    private static AuditEntry Entry(string kind = AuditKinds.Request) =>
        new() { At = DateTimeOffset.UnixEpoch, Kind = kind, Endpoint = "GET /api/me", Status = 200 };

    [Fact]
    public void A_full_queue_drops_and_counts_instead_of_blocking()
    {
        var queue = new AuditQueue(capacity: 2);
        for (var i = 0; i < 5; i++) queue.Enqueue(Entry());
        Assert.Equal(3, queue.Dropped);
    }

    [Fact]
    public void Enqueue_truncates_long_values_and_measures_ms()
    {
        var queue = new AuditQueue();
        queue.Enqueue(AuditKinds.LoginFailure, new string('u', 300), new string('e', 300), 401,
            System.Diagnostics.Stopwatch.GetTimestamp(), TimeProvider.System, rows: 3, cache: "HIT");
        Assert.True(queue.Reader.TryRead(out var e));
        Assert.Equal(256, e!.UserName!.Length);
        Assert.Equal(256, e.Endpoint.Length);
        Assert.Equal(3, e.Rows);
        Assert.Equal("HIT", e.Cache);
        Assert.InRange(e.Ms, 0, 1000);
    }

    [Fact]
    public async Task A_failed_write_is_logged_and_dropped_not_thrown()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<AppDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
        await using var sp = services.BuildServiceProvider();
        var writer = new AuditWriter(new AuditQueue(), sp, Flush(0), Retention(), NullLogger<AuditWriter>.Instance);

        var batch = new List<AuditEntry> { Entry(), Entry() };
        await writer.WriteAsync(batch, TestContext.Current.CancellationToken);
        Assert.Empty(batch);
    }

    [Fact]
    public async Task A_cancelled_write_is_dropped_quietly()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<AppDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none"));
        await using var sp = services.BuildServiceProvider();
        var writer = new AuditWriter(new AuditQueue(), sp, Flush(0), Retention(), NullLogger<AuditWriter>.Instance);

        var batch = new List<AuditEntry> { Entry() };
        await writer.WriteAsync(batch, new CancellationToken(canceled: true));
        Assert.Empty(batch);
        await writer.WriteAsync([], TestContext.Current.CancellationToken); // empty batch is a no-op
    }

    [Fact]
    public async Task The_writer_survives_failed_writes_and_stops_cleanly()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<AppDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
        await using var sp = services.BuildServiceProvider();
        var queue = new AuditQueue();
        var writer = new AuditWriter(queue, sp, Flush(0), Retention(), NullLogger<AuditWriter>.Instance);

        await writer.StartAsync(TestContext.Current.CancellationToken);
        queue.Enqueue(Entry());
        while (queue.Reader.Count > 0) await Task.Delay(10, TestContext.Current.CancellationToken);
        await writer.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(writer.ExecuteTask!.IsCompletedSuccessfully);
    }
}
