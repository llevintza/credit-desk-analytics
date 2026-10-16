using Desk.Api.Limits;
using Desk.Data.Insights;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Api.Tests;

/// <summary>#211: the database permits, plus the insights fan-out and the background headroom, must fit the pool (README §7.2).</summary>
[Collection(ApiCollection.Name)]
public sealed class ConnectionBudgetTests(PostgresApiFactory api)
{
    private static (LimitsOptions Limits, InsightsOptions Insights) Options(params (string Key, string Value)[] pairs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();
        return (LimitsOptions.From(config), InsightsOptions.From(config));
    }

    [Fact]
    public void The_defaults_fit_the_default_pool_exactly()
    {
        var (limits, insights) = Options();
        // 8 permits; 3 of them on insights reads hold the 10 insights connections, 5 hold one each: 15, plus 5.
        Assert.Equal(15, ConnectionBudget.Peak(limits, insights));
        Assert.Equal(20, ConnectionBudget.Needed(limits, insights));
        Assert.Null(ConnectionBudget.Problem(limits, insights, maxPoolSize: 20));
    }

    [Theory]
    [InlineData("8", "10", "4", 15)]
    [InlineData("8", "1", "4", 8)]   // one insights connection: every permit holds at most one
    [InlineData("2", "10", "4", 8)]  // two permits, both on insights reads: 4 + 4
    [InlineData("16", "10", "4", 23)]
    [InlineData("1", "100", "16", 16)]
    public void Peak_is_the_worst_split_of_permits_between_insights_and_other_reads(string global, string insightsMax, string parallelism, int peak)
    {
        var (limits, insights) = Options(("RATE_LIMIT_GLOBAL_CONCURRENCY", global), ("INSIGHTS_MAX_CONNECTIONS", insightsMax), ("INSIGHTS_PARALLELISM", parallelism));
        Assert.Equal(peak, ConnectionBudget.Peak(limits, insights));
    }

    [Theory]
    [InlineData("16", "20")] // permits raised
    [InlineData("8", "19")]  // pool lowered
    public void A_pool_below_the_permits_plus_headroom_is_named_without_connection_details(string global, string pool)
    {
        var (limits, insights) = Options(("RATE_LIMIT_GLOBAL_CONCURRENCY", global));
        var problem = ConnectionBudget.Problem(limits, insights, int.Parse(pool, System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotNull(problem);
        foreach (var name in new[] { "RATE_LIMIT_GLOBAL_CONCURRENCY", "INSIGHTS_MAX_CONNECTIONS", "INSIGHTS_PARALLELISM", "DB_MAX_POOL_SIZE", "README §7.2" })
            Assert.Contains(name, problem);
        Assert.Contains($"DB_MAX_POOL_SIZE ({pool})", problem);
        Assert.DoesNotContain("Host=", problem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_up_warns_once_when_the_pool_is_too_small_and_stays_quiet_when_it_fits()
    {
        var small = new CapturingLoggerProvider();
        await using (var host = api.WithSettings(("DB_MAX_POOL_SIZE", "12")).WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(small))))
        {
            _ = host.Services; // starts the host
            var warning = Assert.Single(small.Lines, l => l.Category == typeof(ConnectionBudgetCheck).FullName);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Contains("DB_MAX_POOL_SIZE (12)", warning.Text);
        }

        var fits = new CapturingLoggerProvider();
        await using (var host = api.WithSettings(("DB_MAX_POOL_SIZE", "20")).WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(fits))))
        {
            _ = host.Services;
            Assert.DoesNotContain(fits.Lines, l => l.Category == typeof(ConnectionBudgetCheck).FullName);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string Category, LogLevel Level, string Text)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
