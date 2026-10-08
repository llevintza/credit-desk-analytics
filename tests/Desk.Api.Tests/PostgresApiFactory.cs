using System.Net;
using System.Net.Http.Json;
using Desk.Api.Auth;
using Desk.Data;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace Desk.Api.Tests;

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<PostgresApiFactory>
{
    public const string Name = "api-postgres";
}

/// <summary>
/// The API in Production mode on a migrated Testcontainers Postgres 17 (README §11), with a fake clock.
/// Shared by every API integration test class; each test uses its own accounts, so they don't interfere.
/// Limits are raised here so unrelated tests never trip them; the limit tests build their own host with
/// the real numbers via <see cref="WithSettings"/>.
/// </summary>
public sealed class PostgresApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "Correct-horse-battery-9";

    private readonly PostgreSqlContainer _pg =
        new PostgreSqlBuilder("postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24").Build();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    public string ConnectionString => _pg.GetConnectionString();

    /// <summary>README §11: the integration database is seeded at scale 0.1 with SEED=42 (2,000 positions per as-of).</summary>
    public static readonly DateOnly AsOf = new(2026, 10, 6);

    public async ValueTask InitializeAsync()
    {
        await _pg.StartAsync();
        await using (var db = NewContext())
            await db.Database.MigrateAsync();
        var output = new StringWriter();
        var code = await Desk.Seeder.SeedRunner.RunAsync(
            new Desk.Seeder.SeedOptions(Seed: 42, Scale: 0.1m, IfChanged: true, Force: false, SizeReportOnly: false, MaxMegabytes: 400, AsOf: AsOf),
            ConnectionString, output, output);
        if (code != 0) throw new InvalidOperationException($"seed failed ({code}): {output}");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _pg.DisposeAsync();
    }

    public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema))
        .Options);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        // Every source (App, Core, …) resolves to the container; CI's own DATABASE_URL must never leak in.
        builder.UseSetting("DATABASE_URL", ConnectionString);
        builder.UseSetting("ConnectionStrings:App", ConnectionString);
        builder.UseSetting("SWAGGER_ENABLED", "true");
        builder.UseSetting("AUDIT_FLUSH_SECONDS", "0"); // write audit rows right away so tests can see them
        builder.UseSetting("RATE_LIMIT_PER_USER_PER_MIN", "100000");
        builder.UseSetting("RATE_LIMIT_PER_USER_BURST", "100000");
        builder.UseSetting("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "100000");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
        });
    }

    /// <summary>A separate host (own limiters, own counters) on the same database and clock.</summary>
    public WebApplicationFactory<Program> WithSettings(params (string Key, string Value)[] settings) =>
        WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings) b.UseSetting(key, value);
        });

    public HttpClient NewClient() => NewClient(this);

    public static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        // https: the session cookie is Secure (__Host- prefix), so the cookie container only sends it over https.
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    public async Task<DeskUser> CreateUserAsync(string role = Roles.Viewer, DateTimeOffset? expiresAt = null, string password = Password)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DeskUser>>();
        var email = $"{role}-{Guid.NewGuid():N}@example.com";
        var user = new DeskUser { UserName = email, Email = email, EmailConfirmed = true, ExpiresAt = expiresAt ?? Time.GetUtcNow().AddDays(30) };
        var created = await users.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    public static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string email, string password = Password) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), TestContext.Current.CancellationToken);

    /// <summary>Logs in and returns the XSRF token the SPA would echo in <c>X-XSRF-TOKEN</c>.</summary>
    public static async Task<string> LoginAsync(HttpClient client, string email, string password = Password)
    {
        var res = await PostLoginAsync(client, email, password);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return XsrfToken(res)!;
    }

    public async Task<(HttpClient Client, string Xsrf, DeskUser User)> SignedInAsync(string role = Roles.Viewer)
    {
        var user = await CreateUserAsync(role);
        var client = NewClient();
        return (client, await LoginAsync(client, user.Email!), user);
    }

    public static string? XsrfToken(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(c => c.Split(';')[0]).Where(c => c.StartsWith(AuthSetup.XsrfCookieName + "=", StringComparison.Ordinal))
                     .Select(c => Uri.UnescapeDataString(c[(AuthSetup.XsrfCookieName.Length + 1)..])).LastOrDefault()
            : null;

    /// <summary>The audit writer is asynchronous: wait (bounded) for rows to show up.</summary>
    public async Task<List<AuditEntry>> WaitForAuditAsync(Func<AuditEntry, bool> match, int atLeast = 1)
    {
        for (var i = 0; i < 100; i++)
        {
            await using var db = NewContext();
            var rows = (await db.Audit.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)).Where(match).ToList();
            if (rows.Count >= atLeast) return rows;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Fail("Audit rows did not appear in time.");
        return [];
    }

    public long ConnectionsOpened(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<DbConnectionCounter>().Opened;
}
