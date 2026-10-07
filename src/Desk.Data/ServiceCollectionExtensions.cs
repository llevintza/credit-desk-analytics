using Desk.Data.App;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>Pooled factory: safe for parallel work (one context per task), no captive dependencies.</summary>
    public static IServiceCollection AddDeskData(this IServiceCollection services, IConfiguration config)
    {
        // Resolved on first use, not at startup: the app must boot (and /health answer) without a database.
        services.AddPooledDbContextFactory<AppDbContext>((_, o) => o.UseNpgsql(
            ConnectionStrings.Resolve(config, ConnectionStrings.App),
            n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)));
        return services;
    }
}
