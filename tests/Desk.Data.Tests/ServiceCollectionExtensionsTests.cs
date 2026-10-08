using Desk.Data;
using Desk.Data.App;
using Desk.Data.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Data.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddDeskData_registers_a_pooled_factory_that_is_safe_to_use_in_parallel()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = "Host=localhost;Database=creditdesk;Username=desk",
            })
            .Build();

        var services = new ServiceCollection();
        Assert.Same(services, services.AddDeskData(config));

        await using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(ctx.SeedMetadata);
            Assert.NotNull(ctx.ColumnCatalog);
        });
        await Task.WhenAll(tasks);
    }

    [Fact]
    public void App_entities_expose_their_columns()
    {
        var meta = new SeedMetadata
        {
            Id = 1, Version = "1.0.0", Seed = 42, Scale = 1.0m,
            CompletedAt = DateTimeOffset.UnixEpoch, DatabaseSizeBytes = 8,
        };
        Assert.Equal(1, meta.Id);
        Assert.Equal("1.0.0", meta.Version);
        Assert.Equal(42, meta.Seed);
        Assert.Equal(1.0m, meta.Scale);
        Assert.Equal(DateTimeOffset.UnixEpoch, meta.CompletedAt);
        Assert.Equal(8, meta.DatabaseSizeBytes);

        var col = new ColumnCatalogEntry
        {
            Name = "cusip", Ordinal = 1, Group = "keys", Kind = "Text",
            Aggregation = "None", Header = "CUSIP",
        };
        Assert.Equal("cusip", col.Name);
        Assert.Equal(1, col.Ordinal);
        Assert.Equal("keys", col.Group);
        Assert.Equal("Text", col.Kind);
        Assert.Equal("None", col.Aggregation);
        Assert.Equal("CUSIP", col.Header);
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public async Task With_only_database_url_ef_and_dapper_share_one_data_source()
    {
        // README §5.1 / #128: production resolves every source to DATABASE_URL; EF must not open a second pool.
        var services = new ServiceCollection().AddDeskData(Config(("DATABASE_URL", "Host=db.internal;Database=creditdesk;Username=desk")));
        await using var sp = services.BuildServiceProvider();
        var registry = (DataSourceRegistry)sp.GetRequiredService<IDataSourceRegistry>();
        Assert.Same(registry.Get(ConnectionStrings.App), registry.Get(ConnectionStrings.Core));

        await using var db = await sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(registry.Get(ConnectionStrings.App).ConnectionString, db.Database.GetDbConnection().ConnectionString);
        Assert.Equal(1, registry.DistinctDataSources);
    }

    [Fact]
    public async Task An_app_override_moves_ef_and_only_ef()
    {
        var services = new ServiceCollection().AddDeskData(Config(
            ("DATABASE_URL", "Host=db.internal;Database=creditdesk;Username=desk"),
            ("ConnectionStrings:App", "Host=app.internal;Database=deskapp;Username=desk")));
        await using var sp = services.BuildServiceProvider();
        var registry = (DataSourceRegistry)sp.GetRequiredService<IDataSourceRegistry>();

        await using var db = await sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        var efConnection = db.Database.GetDbConnection().ConnectionString;
        Assert.Equal(registry.Get(ConnectionStrings.App).ConnectionString, efConnection);
        Assert.Contains("app.internal", efConnection);
        Assert.NotEqual(registry.Get(ConnectionStrings.Core).ConnectionString, efConnection);
        Assert.Equal(2, registry.DistinctDataSources);
    }

    [Fact]
    public void AddDeskData_does_not_connect_at_registration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = "Host=127.0.0.1;Port=1;Database=missing;Username=desk",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddDeskData(config);
        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IDbContextFactory<AppDbContext>>());
    }
}
