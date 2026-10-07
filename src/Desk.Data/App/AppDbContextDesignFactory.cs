using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Desk.Data.App;

/// <summary>
/// Used by `dotnet ef` and by the migrations bundle the deploy pipeline runs.
/// Reads ConnectionStrings__App or DATABASE_URL from the environment (URI or keyword format).
/// There is deliberately no built-in default: credentials only ever come from the environment
/// (a local .env, CI service config, or the production secret), never from source.
/// </summary>
public sealed class AppDbContextDesignFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var cs = ConnectionStrings.Resolve(config, ConnectionStrings.App);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema))
            .Options;
        return new AppDbContext(options);
    }
}
