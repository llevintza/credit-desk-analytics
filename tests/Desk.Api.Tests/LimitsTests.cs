using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Desk.Api.Limits;
using Desk.Data;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Api.Tests;

/// <summary>README §7.2 limits and the §11 "429 with Retry-After" / "503 without a DB connection" tests.</summary>
[Collection(ApiCollection.Name)]
public sealed class LimitsTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_is_limited_to_5_per_minute_per_ip()
    {
        await using var host = api.WithSettings(("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "5"));
        var client = PostgresApiFactory.NewClient(host);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostLoginAsync(client, "nobody@example.com")).StatusCode);

        var limited = await PostgresApiFactory.PostLoginAsync(client, "nobody@example.com");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) is > 0 and <= 60);
        Assert.Equal("Too many requests", (await limited.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
    }

    [Fact]
    public async Task Each_user_gets_a_token_bucket_and_429_says_when_to_retry()
    {
        await using var host = api.WithSettings(("RATE_LIMIT_PER_USER_PER_MIN", "60"), ("RATE_LIMIT_PER_USER_BURST", "3"));
        var user = await api.CreateUserAsync();
        var other = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, user.Email!); // spends one token

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            statuses.Add((await client.GetAsync("/api/me", Ct)).StatusCode);
        var limited = await client.GetAsync("/api/me", Ct);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("1", limited.Headers.GetValues("Retry-After").Single());

        // Another user has their own bucket.
        var otherClient = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(otherClient, other.Email!);
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Static_files_and_health_are_never_rate_limited()
    {
        await using var host = api.WithSettings(("RATE_LIMIT_PER_USER_BURST", "1"));
        var client = PostgresApiFactory.NewClient(host);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
    }

    [Fact]
    public async Task Db_endpoints_share_one_concurrency_limit_then_429()
    {
        var gate = new BlockingCommands();
        await using var host = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("RATE_LIMIT_GLOBAL_CONCURRENCY", "1");
            b.UseSetting("RATE_LIMIT_GLOBAL_QUEUE", "1");
            b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(gate));
        });
        var admin = await api.CreateUserAsync(Roles.Admin);
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, admin.Email!);

        var running = client.GetAsync("/api/health/db", Ct);   // holds the only permit
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var queued = client.GetAsync("/api/health/db", Ct);    // waits in the queue
        await Task.Delay(100, Ct);
        var rejected = await client.GetAsync("/api/health/db", Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("1", rejected.Headers.GetValues("Retry-After").Single());

        gate.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await queued).StatusCode);
    }

    [Fact]
    public async Task Maintenance_mode_is_503_on_every_api_call_without_opening_a_connection()
    {
        await using var host = api.WithSettings(("MAINTENANCE_MODE", "true"));
        var client = PostgresApiFactory.NewClient(host);

        foreach (var (method, path) in new[] { ("GET", "/api/me"), ("POST", "/api/auth/login"), ("GET", "/api/health/db"), ("GET", "/api/nope") })
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = method == "POST" ? JsonContent.Create(new { email = "a@example.com", password = "x" }) : null,
            };
            var res = await client.SendAsync(req, Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            Assert.Equal("300", res.Headers.GetValues("Retry-After").Single());
            Assert.Equal(MaintenanceMode.Detail, (await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail);
        }

        var health = await client.GetFromJsonAsync<HealthResponse>("/health", Ct);
        Assert.True(health!.Maintenance);
        Assert.Equal(0, api.ConnectionsOpened(host));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("yes")]
    [InlineData(null)]
    public void Only_an_exact_true_turns_maintenance_on(string? value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [MaintenanceMode.ConfigKey] = value }).Build();
        Assert.False(MaintenanceMode.IsOn(config));
    }

    [Fact]
    public async Task Health_db_is_admin_only_and_checks_the_database()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().GetAsync("/api/health/db", Ct)).StatusCode);

        var (viewer, _, _) = await api.SignedInAsync(Roles.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/health/db", Ct)).StatusCode);

        var before = api.ConnectionsOpened(api);
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);
        var res = await admin.GetFromJsonAsync<DbHealthResponse>("/api/health/db", Ct);
        Assert.Equal("ok", res!.Status);
        Assert.True(api.ConnectionsOpened(api) > before);
    }

    [Fact]
    public async Task Health_db_is_503_when_the_database_is_unreachable()
    {
        var admin = await api.CreateUserAsync(Roles.Admin);
        // Same session cookie, a host whose database is gone (the cookie and keys were read at login).
        await using var broken = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(new FailingCommands())));
        var cookie = await SessionCookieAsync(admin.Email!);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/health/db");
        req.Headers.Add("Cookie", cookie);
        var res = await PostgresApiFactory.NewClient(broken).SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.DoesNotContain("Host=", await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public void Limits_fall_back_to_defaults_for_missing_zero_or_garbage_values()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RATE_LIMIT_PER_USER_PER_MIN"] = "0",
            ["RATE_LIMIT_GLOBAL_CONCURRENCY"] = "lots",
            ["RATE_LIMIT_GLOBAL_QUEUE"] = "-3",
            ["RATE_LIMIT_PER_USER_BURST"] = "40",
        }).Build();
        Assert.Equal(new LimitsOptions(60, 40, 5, 8, 32), LimitsOptions.From(config));
    }

    private async Task<string> SessionCookieAsync(string email)
    {
        var login = await PostgresApiFactory.PostLoginAsync(api.NewClient(), email);
        return login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(Auth.AuthSetup.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
    }

    /// <summary>Holds every <c>SELECT 1</c> until released, so the test controls how long a permit is held.</summary>
    private sealed class BlockingCommands : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText == "SELECT 1")
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailingCommands : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            command.CommandText == "SELECT 1"
                ? throw new InvalidOperationException("Host=db.internal is unreachable")
                : ValueTask.FromResult(result);
    }
}
