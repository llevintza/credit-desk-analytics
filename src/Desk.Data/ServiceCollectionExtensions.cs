using Desk.Data.App;
using Desk.Data.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>README §7.2 query guard: no API query runs longer than this.</summary>
    public const int CommandTimeoutSeconds = 10;

    /// <summary>
    /// Pooled factory: safe for parallel work (one context per task), no captive dependencies. A scoped
    /// <see cref="AppDbContext"/> is rented from the same pool for request-scoped consumers (Identity stores).
    /// </summary>
    public static IServiceCollection AddDeskData(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<DbConnectionCounter>();
        services.AddSingleton<IInterceptor>(sp => sp.GetRequiredService<DbConnectionCounter>());
        // Resolved on first use, not at startup: the app must boot (and /health answer) without a database.
        services.AddPooledDbContextFactory<AppDbContext>((sp, o) => o
            .UseNpgsql(
                ConnectionStrings.Resolve(config, ConnectionStrings.App),
                n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)
                      .CommandTimeout(CommandTimeoutSeconds))
            .AddInterceptors(sp.GetServices<IInterceptor>()));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        services.AddSingleton<IDataSourceRegistry, DataSourceRegistry>();
        return services;
    }
}
