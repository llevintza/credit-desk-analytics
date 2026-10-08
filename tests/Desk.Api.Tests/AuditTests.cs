using Desk.Api.Audit;
using Desk.Data.App;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Desk.Api.Tests;

/// <summary>README §7.2: audit writes never block or fail the request path.</summary>
public sealed class AuditTests
{
    private static IConfiguration Flush(int? seconds) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [AuditWriter.FlushConfigKey] = seconds?.ToString() })
        .Build();

    [Theory]
    [InlineData(null, 30)]
    [InlineData("-1", 30)]
    [InlineData("soon", 30)]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    public void Flush_interval_defaults_to_30_seconds(string? value, int expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [AuditWriter.FlushConfigKey] = value }).Build();
        var writer = new AuditWriter(new AuditQueue(), new ServiceCollection().BuildServiceProvider(), config, NullLogger<AuditWriter>.Instance);
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
        var writer = new AuditWriter(queue, sp, Flush(3600), NullLogger<AuditWriter>.Instance);

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
        var writer = new AuditWriter(new AuditQueue(), sp, Flush(0), NullLogger<AuditWriter>.Instance);

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
        var writer = new AuditWriter(new AuditQueue(), sp, Flush(0), NullLogger<AuditWriter>.Instance);

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
        var writer = new AuditWriter(queue, sp, Flush(0), NullLogger<AuditWriter>.Instance);

        await writer.StartAsync(TestContext.Current.CancellationToken);
        queue.Enqueue(Entry());
        while (queue.Reader.Count > 0) await Task.Delay(10, TestContext.Current.CancellationToken);
        await writer.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(writer.ExecuteTask!.IsCompletedSuccessfully);
    }
}
