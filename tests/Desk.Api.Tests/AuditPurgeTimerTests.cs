using Desk.Api.Audit;
using Desk.Data.App;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Desk.Api.Tests;

/// <summary>
/// #193: the retention purge also runs on a timer, so idle periods drain old rows. These tests need no database;
/// the backlog and idle-period tests are in <see cref="AuditRetentionTests"/> (Testcontainers).
/// </summary>
public sealed class AuditPurgeTimerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static AuditPurgeTimer Timer(IServiceProvider services, AuditRetention retention, TimeProvider time,
        ILogger<AuditPurgeTimer> logger, string? checkMinutes = null) => new(services,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [AuditPurgeTimer.CheckConfigKey] = checkMinutes }).Build(),
        retention, time, logger);

    /// <summary>A pooled factory pointed at a closed port: every query fails fast.</summary>
    private static ServiceProvider DeadDatabase()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<AppDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1"));
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null, 60, false)]
    [InlineData("", 60, false)]
    [InlineData("0", 60, true)]
    [InlineData("-5", 60, true)]
    [InlineData("hourly", 60, true)]
    [InlineData("1441", 60, true)]
    [InlineData("1", 1, false)]
    [InlineData("15", 15, false)]
    [InlineData("1440", 1440, false)]
    public void The_check_interval_defaults_to_60_minutes_and_ignores_junk(string? value, int expectedMinutes, bool warned)
    {
        var logger = new CapturingLogger();
        using var timer = Timer(new ServiceCollection().BuildServiceProvider(), AuditTests.Retention(), TimeProvider.System, logger, value);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), timer.CheckEvery);
        Assert.Equal(warned, logger.Lines.Any(l => l.Level == LogLevel.Warning && l.Text.Contains(AuditPurgeTimer.CheckConfigKey)));
    }

    [Fact]
    public async Task A_failed_purge_is_logged_and_retried_at_the_next_tick()
    {
        await using var sp = DeadDatabase();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        var logger = new CapturingLogger();
        using var timer = Timer(sp, AuditTests.Retention(time: time), time, logger);

        await timer.StartAsync(Ct);
        Assert.Empty(logger.Lines); // nothing at start: a cold start never touches the database

        time.Advance(timer.CheckEvery); // first tick: due, the database is down
        await WaitForAsync(() => logger.Failures == 1);
        time.Advance(timer.CheckEvery); // next tick: retried, not a day later
        await WaitForAsync(() => logger.Failures == 2);

        await timer.StopAsync(Ct);
        Assert.True(timer.ExecuteTask!.IsCompletedSuccessfully); // the failures never escaped to the host
        Assert.All(logger.Lines, l => Assert.Equal(LogLevel.Warning, l.Level));
    }

    [Fact]
    public async Task A_cancelled_check_is_quiet()
    {
        await using var sp = DeadDatabase();
        var logger = new CapturingLogger();
        using var timer = Timer(sp, AuditTests.Retention(), TimeProvider.System, logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => timer.CheckAsync(new CancellationToken(canceled: true)));
        Assert.Empty(logger.Lines);
    }

    internal static async Task WaitForAsync(Func<bool> condition, object? state = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        Assert.True(condition(), $"timed out: {state}");
    }

    internal sealed class CapturingLogger : ILogger<AuditPurgeTimer>
    {
        public override string ToString() { lock (Lines) return string.Join(" | ", Lines); }

        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public int Failures
        {
            get { lock (Lines) return Lines.Count(l => l.Text.Contains("purge failed")); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
