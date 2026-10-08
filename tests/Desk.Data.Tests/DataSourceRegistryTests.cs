using Desk.Data;
using Desk.Data.Sources;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Desk.Data.Tests;

/// <summary>README §5.1: one NpgsqlDataSource per distinct connection string; per-source overrides by config only.</summary>
public sealed class DataSourceRegistryTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // Exact values, not substrings: "Maximum Pool Size=20" is also a prefix of 200.
    private static NpgsqlConnectionStringBuilder Parsed(DataSourceRegistry registry, string source) =>
        new(registry.Get(source).ConnectionString);

    [Fact]
    public async Task Sources_that_share_DATABASE_URL_share_one_data_source()
    {
        await using var registry = new DataSourceRegistry(Config(new() { ["DATABASE_URL"] = "Host=db;Database=desk;Username=u" }), new DbConnectionCounter());
        var core = registry.Get(ConnectionStrings.Core);
        Assert.Same(core, registry.Get(ConnectionStrings.Market));
        Assert.Same(core, registry.Get(ConnectionStrings.App));
        Assert.Same(core, registry.Get(ConnectionStrings.Core));
        Assert.Equal(1, registry.DistinctDataSources);
    }

    [Fact]
    public void Synchronous_dispose_releases_every_data_source_it_created()
    {
        // The CLI tools dispose their containers synchronously, and EF resolves the registry when its pool is built (#128).
        var registry = new DataSourceRegistry(Config(new()
        {
            ["DATABASE_URL"] = "Host=db;Database=desk;Username=u",
            ["ConnectionStrings:Surveillance"] = "Host=other-db;Database=surv;Username=u",
        }), new DbConnectionCounter());
        var core = registry.Get(ConnectionStrings.Core);
        var surveillance = registry.Get(ConnectionStrings.Surveillance);
        registry.Dispose();
        Assert.Throws<ObjectDisposedException>(() => core.OpenConnection());
        Assert.Throws<ObjectDisposedException>(() => surveillance.OpenConnection());
    }

    [Fact]
    public async Task A_per_source_setting_gets_its_own_data_source()
    {
        await using var registry = new DataSourceRegistry(Config(new()
        {
            ["DATABASE_URL"] = "Host=db;Database=desk;Username=u",
            ["ConnectionStrings:Surveillance"] = "postgres://u@other-db:5432/surv",
        }), new DbConnectionCounter());
        Assert.NotSame(registry.Get(ConnectionStrings.Core), registry.Get(ConnectionStrings.Surveillance));
        Assert.Contains("other-db", registry.Get(ConnectionStrings.Surveillance).ConnectionString);
        Assert.Equal(2, registry.DistinctDataSources);
    }

    [Fact]
    public async Task Data_sources_get_the_10_second_command_timeout()
    {
        await using var registry = new DataSourceRegistry(Config(new() { ["DATABASE_URL"] = "Host=db;Database=desk;Username=u;Command Timeout=300" }), new DbConnectionCounter());
        Assert.Equal(10, Parsed(registry, ConnectionStrings.Core).CommandTimeout);
    }

    [Fact]
    public async Task Data_sources_get_the_default_pool_cap_of_20()
    {
        // #182: Npgsql's own default (100) is above a small Neon compute's max_connections.
        await using var registry = new DataSourceRegistry(Config(new() { ["DATABASE_URL"] = "Host=db;Database=desk;Username=u" }), new DbConnectionCounter());
        Assert.Equal(20, DataSourceRegistry.DefaultMaxPoolSize);
        Assert.Equal(20, Parsed(registry, ConnectionStrings.Core).MaxPoolSize);
    }

    [Fact]
    public async Task DB_MAX_POOL_SIZE_overrides_the_cap_and_the_connection_string()
    {
        await using var registry = new DataSourceRegistry(Config(new()
        {
            ["DATABASE_URL"] = "Host=db;Database=desk;Username=u;Maximum Pool Size=100",
            ["DB_MAX_POOL_SIZE"] = "12",
        }), new DbConnectionCounter());
        Assert.Equal(12, Parsed(registry, ConnectionStrings.Core).MaxPoolSize);
    }

    [Fact]
    public async Task DB_MAX_POOL_SIZE_accepts_up_to_Npgsql_s_own_default_of_100()
    {
        await using var registry = new DataSourceRegistry(Config(new()
        {
            ["DATABASE_URL"] = "Host=db;Database=desk;Username=u",
            ["DB_MAX_POOL_SIZE"] = "100",
        }), new DbConnectionCounter());
        Assert.Equal(100, Parsed(registry, ConnectionStrings.Core).MaxPoolSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("twenty")]
    [InlineData("")]
    [InlineData("101")]
    [InlineData("2147483647")]
    [InlineData("99999999999")]
    public async Task An_out_of_range_or_junk_DB_MAX_POOL_SIZE_falls_back_to_the_default(string value)
    {
        await using var registry = new DataSourceRegistry(Config(new()
        {
            ["DATABASE_URL"] = "Host=db;Database=desk;Username=u;Maximum Pool Size=100",
            ["DB_MAX_POOL_SIZE"] = value,
        }), new DbConnectionCounter());
        Assert.Equal(20, Parsed(registry, ConnectionStrings.Core).MaxPoolSize);
    }

    [Fact]
    public async Task Sources_on_one_database_still_share_one_capped_data_source()
    {
        // The cap is applied before keying, so a per-source setting equal to DATABASE_URL still shares the pool.
        await using var registry = new DataSourceRegistry(Config(new()
        {
            ["DATABASE_URL"] = "Host=db;Database=desk;Username=u",
            ["ConnectionStrings:Market"] = "Host=db;Database=desk;Username=u;MaxPoolSize=50;CommandTimeout=300",
            ["DB_MAX_POOL_SIZE"] = "15",
        }), new DbConnectionCounter());
        Assert.Same(registry.Get(ConnectionStrings.Core), registry.Get(ConnectionStrings.Market));
        Assert.Equal(1, registry.DistinctDataSources);
        Assert.Equal(15, Parsed(registry, ConnectionStrings.Market).MaxPoolSize);
    }

    [Theory]
    [InlineData("Command Timeout=300;Host=db;Database=desk;Username=u")]
    [InlineData("MaxPoolSize=50;Host=db;Database=desk;Username=u")]
    [InlineData("Host=db;Command Timeout=300;Database=desk;Username=u")]
    [InlineData("Username=u;Database=desk;Host=db")]
    public async Task Sources_on_one_database_share_a_pool_wherever_the_normalised_keys_sit(string market)
    {
        // R201-02: the pool key must not depend on key order in the configured string.
        await using var registry = new DataSourceRegistry(Config(new()
        {
            ["DATABASE_URL"] = "Host=db;Database=desk;Username=u",
            ["ConnectionStrings:Market"] = market,
        }), new DbConnectionCounter());
        Assert.Same(registry.Get(ConnectionStrings.Core), registry.Get(ConnectionStrings.Market));
        Assert.Equal(1, registry.DistinctDataSources);
    }

    [Fact]
    public async Task Nothing_resolves_until_first_use_and_a_missing_setting_names_the_variable()
    {
        await using var registry = new DataSourceRegistry(Config(new()), new DbConnectionCounter());
        var ex = Assert.Throws<InvalidOperationException>(() => registry.Get(ConnectionStrings.Pricing));
        Assert.Contains("ConnectionStrings__Pricing", ex.Message);
    }

    [Fact]
    public async Task Open_counts_the_connection()
    {
        var counter = new DbConnectionCounter();
        await using var registry = new DataSourceRegistry(Config(new() { ["DATABASE_URL"] = "Host=127.0.0.1;Port=1;Database=x;Username=u;Timeout=1" }), counter);
        await Assert.ThrowsAnyAsync<Exception>(async () => await registry.OpenAsync(ConnectionStrings.Core, TestContext.Current.CancellationToken));
        Assert.Equal(0, counter.Opened); // only successful opens count
        counter.Record();
        Assert.Equal(1, counter.Opened);
    }
}
