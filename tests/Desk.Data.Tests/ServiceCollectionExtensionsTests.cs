using Desk.Data;
using Desk.Data.App;
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
