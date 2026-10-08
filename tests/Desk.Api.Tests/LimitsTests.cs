using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Desk.Api.Limits;
using Desk.Data;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    /// <summary>TestServer has no socket: this sets the peer a request "arrived from" (X-Test-Peer).</summary>
    private sealed class PeerFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, inner) =>
            {
                if (System.Net.IPAddress.TryParse(http.Request.Headers["X-Test-Peer"].ToString(), out var peer))
                    http.Connection.RemoteIpAddress = peer;
                return inner(http);
            });
            next(app);
        };
    }

    /// <summary>A host as deployed on Render (behind Cloudflare), with the given limits.</summary>
    private WebApplicationFactory<Program> BehindCloudflare(params (string Key, string Value)[] settings) =>
        api.WithWebHostBuilder(b =>
        {
            b.UseSetting("FORWARDEDHEADERS_ENABLED", "true");
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            b.ConfigureTestServices(s => s.AddTransient<IStartupFilter, PeerFromHeader>());
        });

    private const string RenderLb = "10.214.3.7";
    private const string CfEdge = "162.158.90.14";

    private static HttpRequestMessage Via(HttpRequestMessage req, string peer, string? xff = null, string? cf = null)
    {
        req.Headers.Add("X-Test-Peer", peer);
        if (xff is not null) req.Headers.Add("X-Forwarded-For", xff);
        if (cf is not null) req.Headers.Add("CF-Connecting-IP", cf);
        return req;
    }

    private static HttpRequestMessage Login(string peer, string? xff = null, string? cf = null) =>
        Via(new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email = "nobody@example.com", password = "wrong-password-123456" }) }, peer, xff, cf);

    [Fact]
    public async Task Two_clients_behind_one_cloudflare_edge_get_separate_login_windows()
    {
        await using var host = BehindCloudflare(("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "5"));
        var client = PostgresApiFactory.NewClient(host);
        // Client A: its own spoofed XFF prefix, then what Cloudflare and Render append.
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login(RenderLb, $"198.51.100.{i}, 203.0.113.7, {CfEdge}", "203.0.113.7"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Login(RenderLb, $"198.51.100.9, 203.0.113.7, {CfEdge}", "203.0.113.7"), Ct)).StatusCode);

        // Client B through the same edge is not locked out by A.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login(RenderLb, $"203.0.113.8, {CfEdge}", "203.0.113.8"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Spoofed_client_headers_from_an_untrusted_source_share_one_window()
    {
        await using var host = BehindCloudflare(("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "5"));
        var client = PostgresApiFactory.NewClient(host);
        // Straight to the app (peer outside Render), and straight to Render (hop outside Cloudflare): a fresh
        // CF-Connecting-IP / True-Client-IP / XFF each time must not buy a fresh window.
        for (var i = 0; i < 5; i++)
        {
            var req = Login(i % 2 == 0 ? "192.0.2.50" : RenderLb, i % 2 == 0 ? $"198.51.100.{i}, {CfEdge}" : "192.0.2.50", $"198.51.100.{i}");
            req.Headers.Add("True-Client-IP", $"198.51.100.{i + 100}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(req, Ct)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Login("192.0.2.50", null, "198.51.100.200"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Through_cloudflare_without_cf_connecting_ip_rotating_other_headers_buys_nothing()
    {
        await using var host = BehindCloudflare(("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "5"));
        var client = PostgresApiFactory.NewClient(host);
        for (var i = 0; i < 6; i++)
        {
            var req = Login(RenderLb, $"198.51.100.{i}, {CfEdge}");
            req.Headers.Add("True-Client-IP", $"198.51.100.{i + 100}");
            Assert.Equal(i < 5 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, (await client.SendAsync(req, Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Anonymous_callers_behind_one_edge_get_their_own_token_bucket()
    {
        await using var host = BehindCloudflare(("RATE_LIMIT_PER_USER_PER_MIN", "1"), ("RATE_LIMIT_PER_USER_BURST", "2"));
        var client = PostgresApiFactory.NewClient(host);
        HttpRequestMessage Me(string ip) => Via(new HttpRequestMessage(HttpMethod.Get, "/api/me"), RenderLb, $"{ip}, {CfEdge}", ip);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Me("203.0.113.7"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Me("203.0.113.7"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Me("203.0.113.7"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Me("203.0.113.8"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Behind_the_proxy_start_up_and_the_first_rejection_are_logged_without_client_addresses()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = BehindCloudflare(("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "1")).WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(logs)));
        var client = PostgresApiFactory.NewClient(host);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login(RenderLb, $"203.0.113.7, {CfEdge}", "203.0.113.7"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Login(RenderLb, $"203.0.113.7, {CfEdge}", "203.0.113.7"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Login(RenderLb, $"203.0.113.7, {CfEdge}", "203.0.113.7"), Ct)).StatusCode);

        var lines = logs.Lines.Where(l => l.Category == typeof(ClientAddressDiagnostics).FullName).Select(l => l.Text).ToList();
        Assert.Contains(lines, l => l.StartsWith("Client address resolver: BehindProxy=True", StringComparison.Ordinal) && l.Contains("ForwardedHeaders=XForwardedProto", StringComparison.Ordinal));
        var rejected = Assert.Single(lines, l => l.StartsWith("First rate-limited request:", StringComparison.Ordinal));
        Assert.Contains("Route=/api/auth/login", rejected);
        Assert.Contains("EndpointPolicy=login", rejected);
        Assert.Contains("Source=CfConnectingIp", rejected);
        Assert.Contains("ForwardedForShape=public>cf", rejected);
        Assert.DoesNotContain("203.0.113.7", string.Join('\n', lines));
    }

    [Fact]
    public async Task A_signed_in_users_429_leaves_the_first_rejection_line_for_anonymous_callers()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = BehindCloudflare(("RATE_LIMIT_PER_USER_PER_MIN", "1"), ("RATE_LIMIT_PER_USER_BURST", "2")).WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(logs)));
        List<string> Rejections() => [.. logs.Lines.Where(l => l.Category == typeof(ClientAddressDiagnostics).FullName && l.Text.StartsWith("First rate-limited request:", StringComparison.Ordinal)).Select(l => l.Text)];

        var user = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(user, (await api.CreateUserAsync()).Email!);
        HttpStatusCode last = default;
        for (var i = 0; i < 4 && last != HttpStatusCode.TooManyRequests; i++)
            last = (await user.GetAsync("/api/me", Ct)).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.Empty(Rejections());

        var anonymous = PostgresApiFactory.NewClient(host);
        HttpRequestMessage Me() => Via(new HttpRequestMessage(HttpMethod.Get, "/api/me"), RenderLb, $"203.0.113.9, {CfEdge}", "203.0.113.9");
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Me(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.SendAsync(Me(), Ct)).StatusCode);
        Assert.Contains("EndpointPolicy=global", Assert.Single(Rejections()));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string Category, string Text)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue((category, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task Behind_the_proxy_the_host_forwards_proto_but_not_the_client_address()
    {
        await using var host = BehindCloudflare();
        Assert.Equal(ForwardedHeaders.XForwardedProto, host.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value.ForwardedHeaders);
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

        // Rejected requests never reach the audit table (no DB writes for a client hammering the limiter).
        await api.WaitForAuditAsync(a => a.UserName == user.Email && a.Kind == Data.App.AuditKinds.Request, atLeast: 3);
        await Task.Delay(300, Ct);
        await using (var db = api.NewContext())
            Assert.DoesNotContain(db.Audit.Where(a => a.UserName == user.Email).Select(a => a.Status).ToList(), s => s == 429);

        // Another user has their own bucket.
        var otherClient = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(otherClient, other.Email!);
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync("/api/me", Ct)).StatusCode);
    }

    private async Task<HttpClient> SignedInOnAsync(WebApplicationFactory<Program> host, string role = Roles.Viewer)
    {
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync(role)).Email!);
        return client;
    }

    [Fact]
    public async Task Login_also_waits_for_the_shared_database_limit()
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
        // Signed in before the only permit is taken: login itself needs one.
        var other = await SignedInOnAsync(host);

        var running = client.GetAsync("/api/health/db", Ct);   // holds the only permit
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // Another user fills the shared queue (the same user would wait in their own per-user queue instead).
        var queued = other.GetAsync("/api/me", Ct);
        await Task.Delay(100, Ct);
        var login = await PostgresApiFactory.PostLoginAsync(PostgresApiFactory.NewClient(host), admin.Email!);
        Assert.Equal(HttpStatusCode.TooManyRequests, login.StatusCode);

        gate.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await queued).StatusCode);
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

        // Three different users: the shared limit is across users (one user's own requests queue per user first).
        // They sign in before the only permit is taken: login itself needs one.
        var second = await SignedInOnAsync(host, Roles.Admin);
        var third = await SignedInOnAsync(host, Roles.Admin);
        var running = client.GetAsync("/api/health/db", Ct);   // holds the only permit
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var queued = second.GetAsync("/api/health/db", Ct);    // waits in the queue
        await Task.Delay(100, Ct);
        var rejected = await third.GetAsync("/api/health/db", Ct);

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

        // A signed-in browser still sends its session cookie: /health, the SPA and Swagger must not decrypt it
        // (key ring + security-stamp query would wake the database).
        var user = await api.CreateUserAsync(Roles.Admin);
        var cookie = (await PostgresApiFactory.PostLoginAsync(api.NewClient(), user.Email!)).Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(Auth.AuthSetup.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
        foreach (var path in new[] { "/health", "/positions", "/swagger/index.html", "/openapi/v1.json" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Add("Cookie", cookie);
            var res = await client.SendAsync(req, Ct);
            Assert.Equal(path.StartsWith("/health") || path == "/positions" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, res.StatusCode);
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
            ["RATE_LIMIT_PER_USER_CONCURRENCY"] = "0",
            ["RATE_LIMIT_PER_USER_QUEUE"] = "3",
            ["EXPORT_GLOBAL_SLOTS"] = "x",
            ["EXPORT_TIMEOUT_SECONDS"] = "-1",
        }).Build();
        Assert.Equal(new LimitsOptions(60, 40, 5, 8, 32, 1, 3, 2, TimeSpan.FromSeconds(60)), LimitsOptions.From(config));
        foreach (var bad in new[] { "0", "NaN", "Infinity", "soon", "", "3600.5", "1e7", "1e300" })
            Assert.Equal(TimeSpan.FromSeconds(60), LimitsOptions.From(Config(("EXPORT_TIMEOUT_SECONDS", bad))).ExportTimeout);
        Assert.Equal(TimeSpan.FromSeconds(0.5), LimitsOptions.From(Config(("EXPORT_TIMEOUT_SECONDS", "0.5"))).ExportTimeout);
        Assert.Equal(TimeSpan.FromHours(1), LimitsOptions.From(Config(("EXPORT_TIMEOUT_SECONDS", "3600"))).ExportTimeout);
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void Export_gate_allows_one_per_user_and_a_few_in_total()
    {
        var gate = new ExportGate(LimitsOptions.From(Config(("EXPORT_GLOBAL_SLOTS", "2"))));
        Assert.Equal(ExportGate.Result.Started, gate.TryBegin("a"));
        Assert.Equal(ExportGate.Result.UserBusy, gate.TryBegin("a"));
        Assert.Equal(ExportGate.Result.Started, gate.TryBegin("b"));
        Assert.Equal(ExportGate.Result.Full, gate.TryBegin("c"));
        Assert.False(gate.IsRunning("c")); // a refused start leaves nothing behind
        Assert.Equal(2, gate.Running);
        gate.End("c");                      // ending what never started frees nothing
        Assert.Equal(2, gate.Running);
        gate.End("a");
        Assert.Equal(ExportGate.Result.Started, gate.TryBegin("c"));
        gate.End("b");
        gate.End("c");
        Assert.Equal(0, gate.Running);
    }

    private async Task<string> SessionCookieAsync(string email)
    {
        var login = await PostgresApiFactory.PostLoginAsync(api.NewClient(), email);
        return login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(Auth.AuthSetup.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
    }

    // ---- #127: per-user concurrency, export cap and deadline, cold meta load ----

    [Fact]
    public async Task One_request_per_user_in_flight_with_a_small_queue_and_other_users_unaffected()
    {
        var gate = new BlockingCommands();
        await using var host = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("RATE_LIMIT_PER_USER_QUEUE", "1");
            b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(gate));
        });
        var admin = await api.CreateUserAsync(Roles.Admin);
        var client = PostgresApiFactory.NewClient(host);
        var xsrf = await PostgresApiFactory.LoginAsync(client, admin.Email!);

        var running = client.GetAsync("/api/health/db", Ct);   // the user's one permit, held in the database
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var queued = client.GetAsync("/api/me", Ct);           // the user's one queue place
        await Task.Delay(100, Ct);
        var rejected = await client.GetAsync("/api/me", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.False(queued.IsCompleted);

        // Another user isn't behind this one.
        var other = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(other, (await api.CreateUserAsync()).Email!);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/me", Ct)).StatusCode);

        // Nor is the same user's export: exports are capped by the export gate, not by this queue.
        using (var export = new HttpRequestMessage(HttpMethod.Post, "/api/positions/export") { Content = JsonContent.Create(new { columns = new[] { "deal_name" } }) })
        {
            export.Headers.Add("X-XSRF-TOKEN", xsrf);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(export, Ct)).StatusCode);
        }
        // However routing lets the path be spelled.
        using (var export = new HttpRequestMessage(HttpMethod.Post, "/API/Positions/Export/") { Content = JsonContent.Create(new { columns = new[] { "deal_name" } }) })
        {
            export.Headers.Add("X-XSRF-TOKEN", xsrf);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(export, Ct)).StatusCode);
        }

        gate.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await queued).StatusCode);
    }

    [Fact]
    public async Task A_request_waiting_in_its_users_queue_holds_no_database_permit()
    {
        var gate = new BlockingCommands();
        await using var host = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("RATE_LIMIT_GLOBAL_CONCURRENCY", "2");
            b.UseSetting("RATE_LIMIT_GLOBAL_QUEUE", "1");
            b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(gate));
        });
        var admin = await api.CreateUserAsync(Roles.Admin);
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, admin.Email!);
        var other = await SignedInOnAsync(host); // before any permit is taken: login needs one

        var running = client.GetAsync("/api/health/db", Ct);   // A's one permit, and one of the two database permits
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var queued = new[] { client.GetAsync("/api/me", Ct), client.GetAsync("/api/me", Ct) }; // in A's own queue
        await Task.Delay(100, Ct);

        // Had A's queued requests taken the second database permit and the shared queue place, B would get a 429.
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/me", Ct)).StatusCode);
        Assert.All(queued, q => Assert.False(q.IsCompleted));

        gate.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        foreach (var q in queued)
            Assert.Equal(HttpStatusCode.OK, (await q).StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_are_not_held_to_one_request_in_flight()
    {
        // Anonymous calls from one address share a key; with a per-key cap of 1 and a queue of 1, the third would be
        // a 429. They wait only for the shared database limiter, then get their 401.
        var gate = new BlockingCommands();
        await using var host = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("RATE_LIMIT_PER_USER_QUEUE", "1");
            b.UseSetting("RATE_LIMIT_GLOBAL_CONCURRENCY", "1");
            b.UseSetting("RATE_LIMIT_GLOBAL_QUEUE", "8");
            b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(gate));
        });
        var admin = await api.CreateUserAsync(Roles.Admin);
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, admin.Email!);

        var running = client.GetAsync("/api/health/db", Ct);   // holds the only database permit
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var anonymous = PostgresApiFactory.NewClient(host);
        var calls = Enumerable.Range(0, 3).Select(_ => anonymous.GetAsync("/api/me", Ct)).ToArray();
        await Task.Delay(100, Ct);

        gate.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        foreach (var call in calls)
            Assert.Equal(HttpStatusCode.Unauthorized, (await call).StatusCode);
    }

    [Fact]
    public async Task A_request_queued_behind_its_own_user_spends_one_token()
    {
        // No refill during the test: 4 tokens are all this user gets.
        var gate = new BlockingCommands();
        await using var host = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("RATE_LIMIT_PER_USER_PER_MIN", "1");
            b.UseSetting("RATE_LIMIT_PER_USER_BURST", "4");
            b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(gate));
        });
        var admin = await api.CreateUserAsync(Roles.Admin);
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, admin.Email!);

        var running = client.GetAsync("/api/health/db", Ct);   // holds the user's one permit
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = client.GetAsync("/api/me", Ct);           // both queue behind it
        var third = client.GetAsync("/api/me", Ct);
        await Task.Delay(100, Ct);

        gate.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await third).StatusCode);
    }

    [Fact]
    public void Only_a_routed_export_endpoint_is_exempt_from_the_per_user_limiter()
    {
        var unrouted = new DefaultHttpContext();
        unrouted.Request.Path = "/api/positions/export";
        Assert.False(RateLimiting.IsExport(unrouted)); // no endpoint matched: the path alone exempts nothing

        var other = new DefaultHttpContext();
        other.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(), "other"));
        Assert.False(RateLimiting.IsExport(other));

        var export = new DefaultHttpContext();
        export.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(ExportEndpoint.Instance), "export"));
        Assert.True(RateLimiting.IsExport(export));
    }

    private static HttpRequestMessage Export(string xsrf, params string[] columns)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/positions/export") { Content = JsonContent.Create(new { columns }) };
        req.Headers.Add("X-XSRF-TOKEN", xsrf);
        return req;
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
            await Task.Delay(50, Ct);
        Assert.True(done(), "the export did not release its slots in time");
    }

    [Fact]
    public async Task A_third_concurrent_export_from_another_user_is_429()
    {
        await using var host = api.WithSettings(("EXPORT_GLOBAL_SLOTS", "2"));
        var gate = host.Services.GetRequiredService<ExportGate>();
        Assert.Equal(ExportGate.Result.Started, gate.TryBegin("first@example.com"));
        Assert.Equal(ExportGate.Result.Started, gate.TryBegin("second@example.com"));
        try
        {
            var client = PostgresApiFactory.NewClient(host);
            var xsrf = await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);
            var res = await client.SendAsync(Export(xsrf, "deal_name"), Ct);
            Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
            Assert.Equal("10", res.Headers.GetValues("Retry-After").Single());
            Assert.Equal("Too many exports", (await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
            Assert.Equal(2, gate.Running); // the refused export took nothing
        }
        finally
        {
            gate.End("first@example.com");
            gate.End("second@example.com");
        }
    }

    [Fact]
    public async Task An_export_past_its_deadline_before_any_output_is_503_and_releases_its_slots()
    {
        await using var host = api.WithSettings(("EXPORT_TIMEOUT_SECONDS", "0.001"));
        var user = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);
        var xsrf = await PostgresApiFactory.LoginAsync(client, user.Email!);

        var res = await client.SendAsync(Export(xsrf, "deal_name"), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("Export timed out", (await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
        Assert.Null(res.Content.Headers.ContentDisposition); // a problem, not a file
        Assert.False(res.Headers.Contains("Retry-After"));  // the same export would time out again
        var gate = host.Services.GetRequiredService<ExportGate>();
        Assert.False(gate.IsRunning(user.Email!));
        Assert.Equal(0, gate.Running);
    }

    /// <summary>Every catalog column: the CSV is far larger than TestServer's 64 KB response pipe.</summary>
    private async Task<string[]> AllColumnsAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElementList>("/api/meta/columns", Ct))!.Select(c => c.GetProperty("name").GetString()!).Where(n => n != "position_id")];

    private sealed class JsonElementList : List<System.Text.Json.JsonElement>;

    [Fact]
    public async Task A_stalled_reader_hits_the_deadline_the_download_breaks_and_the_slots_are_released()
    {
        await using var host = api.WithSettings(("EXPORT_TIMEOUT_SECONDS", "1"));
        var user = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);
        var xsrf = await PostgresApiFactory.LoginAsync(client, user.Email!);
        var gate = host.Services.GetRequiredService<ExportGate>();

        // Headers arrive with the first flushed chunk; then nothing is read, so the server's writes block (backpressure)
        // until the deadline fires.
        using var res = await client.SendAsync(Export(xsrf, await AllColumnsAsync(client)), HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(gate.IsRunning(user.Email!));
        await WaitUntilAsync(() => !gate.IsRunning(user.Email!) && gate.Running == 0);

        // The partial file never ends like a complete one: reading it to the end fails.
        await Assert.ThrowsAnyAsync<Exception>(async () => await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_client_that_goes_away_releases_the_export_slots()
    {
        await using var host = api.WithSettings(("EXPORT_TIMEOUT_SECONDS", "60"));
        var user = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);
        var xsrf = await PostgresApiFactory.LoginAsync(client, user.Email!);
        var gate = host.Services.GetRequiredService<ExportGate>();

        var res = await client.SendAsync(Export(xsrf, await AllColumnsAsync(client)), HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.True(gate.IsRunning(user.Email!));
        res.Dispose(); // the browser tab closed: the request is aborted while the server is blocked writing
        await WaitUntilAsync(() => !gate.IsRunning(user.Email!) && gate.Running == 0);

        // And the user can export again straight away.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Export(xsrf, "deal_name"), Ct)).StatusCode);
    }

    /// <summary>Tracks how many database connections are open at once, EF (interceptor) and Dapper (registry) alike.</summary>
    private sealed class OpenConnections : DbConnectionInterceptor
    {
        private int _open;
        private int _max;
        public int Max => Volatile.Read(ref _max);

        public void Opened()
        {
            var now = Interlocked.Increment(ref _open);
            int max;
            while (now > (max = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, max) != max) { }
        }

        public void Closed() => Interlocked.Decrement(ref _open);

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Opened();
            return Task.CompletedTask;
        }

        public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
        {
            Closed();
            return Task.CompletedTask;
        }
    }

    private sealed class TrackedSources(Desk.Data.Sources.DataSourceRegistry inner, OpenConnections open) : Desk.Data.Sources.IDataSourceRegistry
    {
        public Npgsql.NpgsqlDataSource Get(string source) => inner.Get(source);

        public async ValueTask<Npgsql.NpgsqlConnection> OpenAsync(string source, CancellationToken ct)
        {
            var conn = await inner.OpenAsync(source, ct);
            open.Opened();
            conn.StateChange += (_, e) => { if (e.CurrentState == System.Data.ConnectionState.Closed) open.Closed(); };
            return conn;
        }
    }

    [Fact]
    public async Task A_cold_meta_load_holds_one_connection_at_a_time()
    {
        // The request that loads the meta snapshot holds one database permit, so it must not hold four connections.
        var open = new OpenConnections();
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IInterceptor>(open);
            s.AddSingleton<Desk.Data.Sources.DataSourceRegistry>();
            s.AddSingleton<Desk.Data.Sources.IDataSourceRegistry>(sp => new TrackedSources(sp.GetRequiredService<Desk.Data.Sources.DataSourceRegistry>(), open));
        }));
        var cache = new Desk.Api.Positions.MetaCache(host.Services.GetRequiredService<Desk.Data.Grid.MetaRepository>(), api.Time);
        var (snapshot, loadMs) = await cache.GetWithStatusAsync(Ct);
        Assert.NotNull(loadMs);
        Assert.True(snapshot.HasData);
        Assert.Equal(1, open.Max);
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
