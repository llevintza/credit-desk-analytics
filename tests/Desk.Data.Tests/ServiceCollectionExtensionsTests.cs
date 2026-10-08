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
            Assert.NotNull(ctx);
        });
        await Task.WhenAll(tasks);
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
