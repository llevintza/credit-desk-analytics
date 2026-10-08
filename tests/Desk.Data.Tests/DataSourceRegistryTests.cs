using Desk.Data;
using Desk.Data.Sources;
using Microsoft.Extensions.Configuration;

namespace Desk.Data.Tests;

/// <summary>README §5.1: one NpgsqlDataSource per distinct connection string; per-source overrides by config only.</summary>
public sealed class DataSourceRegistryTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

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
        Assert.Contains("Command Timeout=10", registry.Get(ConnectionStrings.Core).ConnectionString);
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
