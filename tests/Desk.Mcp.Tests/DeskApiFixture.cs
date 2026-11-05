using Desk.Api.Auth;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace Desk.Mcp.Tests;

[CollectionDefinition(Name)]
public sealed class DeskApiCollection : ICollectionFixture<DeskApiFixture>
{
    public const string Name = "mcp-api-postgres";
}

/// <summary>
/// The local test API for the MCP server: Desk.Api in Production mode on a migrated, seeded Testcontainers Postgres 17
/// (SEED=42, scale 0.1, as in tests/Desk.Api.Tests). Each test creates its own account with a generated password and,
/// where it needs one, its own host (<see cref="Host"/>) with a stub entitlement provider or a fake clock.
/// </summary>
public sealed class DeskApiFixture : IAsyncLifetime
{
    /// <summary>Generated per run (the app's own generator), never a literal in the repository.</summary>
    public static readonly string Password = PasswordGenerator.Generate();

    public static readonly DateOnly AsOf = new(2026, 10, 6);

    private readonly PostgreSqlContainer _pg =
        new PostgreSqlBuilder("postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24").Build();

    private readonly List<WebApplicationFactory<global::Program>> _hosts = [];

    public string ConnectionString => _pg.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _pg.StartAsync();
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)).Options))
            await db.Database.MigrateAsync();
        var output = new StringWriter();
        var code = await Desk.Seeder.SeedRunner.RunAsync(
            new Desk.Seeder.SeedOptions(Seed: 42, Scale: 0.1m, IfChanged: true, Force: false, SizeReportOnly: false, MaxMegabytes: 400, AsOf: AsOf),
            ConnectionString, output, output);
        if (code != 0) throw new InvalidOperationException($"seed failed ({code}): {output}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
        await _pg.DisposeAsync();
    }

    /// <summary>A new API host on the shared database, with <paramref name="services"/> applied after the app's own.</summary>
    public WebApplicationFactory<global::Program> Host(Action<IServiceCollection>? services = null, TimeProvider? time = null)
    {
        var host = new DeskApi(ConnectionString, s =>
        {
            if (time is not null)
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton(time);
            }
            services?.Invoke(s);
        });
        _hosts.Add(host);
        return host;
    }

    /// <summary>A viewer account with a generated email and <see cref="Password"/>; returns the email.</summary>
    public static async Task<string> CreateUserAsync(WebApplicationFactory<global::Program> host, DateTimeOffset now)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DeskUser>>();
        var email = $"mcp-{Guid.NewGuid():N}@example.com";
        var user = new DeskUser { UserName = email, Email = email, EmailConfirmed = true, ExpiresAt = now.AddDays(30) };
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        Assert.True((await users.AddToRoleAsync(user, Roles.Viewer)).Succeeded);
        return email;
    }

    private sealed class DeskApi(string connectionString, Action<IServiceCollection> services) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            // Every source resolves to the container; a DATABASE_URL in the environment must never leak in.
            builder.UseSetting("DATABASE_URL", connectionString);
            builder.UseSetting("ConnectionStrings:App", connectionString);
            builder.UseSetting("AUDIT_FLUSH_SECONDS", "0");
            builder.UseSetting("RATE_LIMIT_PER_USER_PER_MIN", "100000");
            builder.UseSetting("RATE_LIMIT_PER_USER_BURST", "100000");
            builder.UseSetting("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "100000");
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton(new LoginFloorOptions(TimeSpan.Zero, TimeSpan.Zero));
                services(s);
            });
        }
    }
}
