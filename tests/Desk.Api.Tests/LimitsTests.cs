using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Desk.Api.Auth;
using Desk.Api.Limits;
using Desk.Api.Positions;
using Desk.Data;
using Desk.Data.Auth;
using Desk.Data.Grid;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
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

    [Theory]
    [InlineData("172.20.0.4", "")]                 // #165: a Render peer outside 10.0.0.0/8
    [InlineData(RenderLb, ", 10.1.2.3, 100.64.0.7")] // #165: Render's internal hops appended after the edge
    [InlineData("100.64.9.9", ", fd00::5")]
    public async Task Two_clients_get_separate_login_windows_whatever_render_s_private_hops_look_like(string peer, string internalHops)
    {
        await using var host = BehindCloudflare(("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "5"));
        var client = PostgresApiFactory.NewClient(host);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login(peer, $"198.51.100.{i}, 203.0.113.7, {CfEdge}{internalHops}", "203.0.113.7"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Login(peer, $"203.0.113.7, {CfEdge}{internalHops}", "203.0.113.7"), Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login(peer, $"203.0.113.8, {CfEdge}{internalHops}", "203.0.113.8"), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_hop_listed_in_client_address_trusted_hops_is_skipped_so_clients_behind_it_are_apart()
    {
        const string ingress = "198.51.100.10"; // stands in for a public proxy hop between Cloudflare and the app
        await using (var unlisted = BehindCloudflare(("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "5")))
        {
            // Unlisted, the hop is the key: every client behind it shares one window (and the fallback is warned).
            var client = PostgresApiFactory.NewClient(unlisted);
            for (var i = 0; i < 5; i++)
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login(RenderLb, $"203.0.113.{i}, {CfEdge}, {ingress}", $"203.0.113.{i}"), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Login(RenderLb, $"203.0.113.9, {CfEdge}, {ingress}", "203.0.113.9"), Ct)).StatusCode);
        }

        await using var listed = BehindCloudflare(("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "5"), (ClientAddress.TrustedHopsKey, "198.51.100.0/24"));
        var listedClient = PostgresApiFactory.NewClient(listed);
        for (var i = 0; i < 6; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await listedClient.SendAsync(Login(RenderLb, $"203.0.113.{i}, {CfEdge}, {ingress}", $"203.0.113.{i}"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Forged_edges_and_private_hops_from_a_client_that_reaches_render_directly_share_one_window()
    {
        // Regression for #165's widened rules: the client's own address is what Render appended, rightmost, so a
        // fresh forged CF-Connecting-IP, edge or private hop on each request still lands in its one window.
        await using var host = BehindCloudflare(("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "5"));
        var client = PostgresApiFactory.NewClient(host);
        for (var i = 0; i < 6; i++)
        {
            var res = await client.SendAsync(Login(RenderLb, $"198.51.100.{i}, {CfEdge}, 10.0.0.{i}, 192.0.2.50", $"198.51.100.{i + 100}"), Ct);
            Assert.Equal(i < 5 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, res.StatusCode);
        }
    }

    [Fact]
    public async Task Ipv6_clients_in_one_64_share_a_login_window_and_other_64s_do_not()
    {
        // #164: rotating the low 64 bits of an IPv6 address must not buy a fresh window.
        await using var host = BehindCloudflare(("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "5"));
        var client = PostgresApiFactory.NewClient(host);
        const string edge = "2606:4700:10::ac43:1b0a";
        for (var i = 0; i < 5; i++)
        {
            var ip = $"2001:db8:7:8::{i + 1:x}";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login(RenderLb, $"{ip}, {edge}", ip), Ct)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Login(RenderLb, $"2001:db8:7:8:ab::1, {edge}", "2001:db8:7:8:ab::1"), Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login(RenderLb, $"2001:db8:7:9::1, {edge}", "2001:db8:7:9::1"), Ct)).StatusCode);
    }

    [Fact]
    public async Task An_ipv4_mapped_peer_and_the_same_plain_ipv4_peer_share_a_login_window()
    {
        await using var host = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "5");
            b.ConfigureTestServices(s => s.AddTransient<IStartupFilter, PeerFromHeader>());
        });
        var client = PostgresApiFactory.NewClient(host);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login(i % 2 == 0 ? "203.0.113.9" : "::ffff:203.0.113.9"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Login("::ffff:203.0.113.9"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Login("203.0.113.9"), Ct)).StatusCode);
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

    /// <summary>Signs <paramref name="user"/> in, then renames the account: the first session keeps the old name (#185).</summary>
    private async Task<(HttpClient Before, HttpClient After)> SignedInBeforeAndAfterARenameAsync(WebApplicationFactory<Program> host, DeskUser user)
    {
        var before = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(before, user.Email!);
        var renamed = $"renamed-{user.UserName}";
        await using (var db = api.NewContext())
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.UserName, renamed).SetProperty(u => u.NormalizedUserName, renamed.ToUpperInvariant()), Ct);
        var after = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(after, user.Email!);
        Assert.NotEqual(
            (await before.GetFromJsonAsync<Auth.MeResponse>("/api/me", Ct))!.Email,
            (await after.GetFromJsonAsync<Auth.MeResponse>("/api/me", Ct))!.Email);
        return (before, after);
    }

    [Fact]
    public async Task Two_sessions_of_one_account_share_its_token_bucket_across_a_rename()
    {
        // #185: keyed on the user id, not the user name, a renamed account can't get a fresh bucket.
        await using var host = api.WithSettings(("RATE_LIMIT_PER_USER_PER_MIN", "1"), ("RATE_LIMIT_PER_USER_BURST", "4"));
        var (before, after) = await SignedInBeforeAndAfterARenameAsync(host, await api.CreateUserAsync()); // 2 of 4

        Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/api/me", Ct)).StatusCode); // 3 of 4
        Assert.Equal(HttpStatusCode.OK, (await after.GetAsync("/api/me", Ct)).StatusCode);  // 4 of 4
        Assert.Equal(HttpStatusCode.TooManyRequests, (await before.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await after.GetAsync("/api/me", Ct)).StatusCode);
    }

    /// <summary>
    /// A live, issued token whose principal has lost its account id after authentication (#361). Issued tokens always
    /// carry one, so this is how a test reaches the broken-session path through the real pipeline.
    /// </summary>
    private sealed class DropAccountId : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal) =>
            Task.FromResult(new ClaimsPrincipal(principal.Identities.Select(i =>
                new ClaimsIdentity(i.Claims.Where(c => c.Type != ClaimTypes.NameIdentifier), i.AuthenticationType, i.NameClaimType, i.RoleClaimType))));
    }

    private static async Task AssertNoAccountIdAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(res.Headers.Contains("Retry-After"));
        Assert.Equal(Desk.Api.Auth.PrincipalExtensions.NoAccountIdTitle, (await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
    }

    [Fact]
    public async Task A_signed_in_caller_without_an_account_id_is_refused_and_never_keyed_on_its_address()
    {
        // #361: keyed on the address, such callers would spend (and share) the anonymous callers' buckets there.
        await using var host = BehindCloudflare(("RATE_LIMIT_PER_USER_PER_MIN", "1"), ("RATE_LIMIT_PER_USER_BURST", "2"))
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IClaimsTransformation, DropAccountId>()));
        var client = PostgresApiFactory.NewClient(host);
        HttpRequestMessage From(HttpRequestMessage req, string ip) => Via(req, RenderLb, $"{ip}, {CfEdge}", ip);
        async Task<string> SignInFromAsync(string ip)
        {
            var user = await api.CreateUserAsync();
            var login = await client.SendAsync(From(new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new LoginRequest(user.Email!, PostgresApiFactory.Password)),
            }, ip), Ct);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            return (await login.Content.ReadFromJsonAsync<AccessTokenResponse>(Ct))!.AccessToken;
        }
        HttpRequestMessage Me(string ip, string? token = null)
        {
            var req = From(new HttpRequestMessage(HttpMethod.Get, "/api/me"), ip);
            if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return req;
        }

        // Signed in elsewhere, so the addresses below start with full anonymous buckets (2 each).
        var first = await SignInFromAsync("198.51.100.20");
        var second = await SignInFromAsync("198.51.100.21");
        for (var i = 0; i < 3; i++) // more than a burst: nothing is granted, so nothing runs out
        {
            await AssertNoAccountIdAsync(await client.SendAsync(Me("203.0.113.7", first), Ct));
            await AssertNoAccountIdAsync(await client.SendAsync(Me("203.0.113.8", second), Ct));
        }

        // The anonymous buckets at both addresses are untouched: 2 calls each, then 429.
        foreach (var ip in new[] { "203.0.113.7", "203.0.113.8" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Me(ip), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Me(ip), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Me(ip), Ct)).StatusCode);
        }
        // And still refused as a broken session, not as an address out of tokens.
        await AssertNoAccountIdAsync(await client.SendAsync(Me("203.0.113.7", first), Ct));
    }

    [Fact]
    public async Task An_export_without_an_account_id_is_401_and_never_enters_the_export_gate()
    {
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IClaimsTransformation, DropAccountId>()));
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);

        var res = await client.PostAsJsonAsync("/api/positions/export", new { columns = new[] { "deal_name" } }, Ct);
        await AssertNoAccountIdAsync(res);
        Assert.Equal(0, host.Services.GetRequiredService<ExportGate>().Running);
    }

    [Fact]
    public async Task The_export_handler_refuses_a_principal_without_an_account_id_by_itself()
    {
        // The limiter refuses first; the handler checks again, so a request that got past it can't key the gate on null.
        var services = api.Services;
        var gate = new ExportGate(services.GetRequiredService<LimitsOptions>());
        foreach (var user in new[]
                 {
                     new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u@example.com")], "test")),
                     new ClaimsPrincipal(new ClaimsIdentity()),
                 })
        {
            var http = new DefaultHttpContext { User = user, RequestServices = services };
            var result = await PositionsEndpoints.ExportAsync(new GridRequest(Columns: ["deal_name"]), http,
                services.GetRequiredService<MetaCache>(), services.GetRequiredService<IPortfolioEntitlements>(),
                services.GetRequiredService<GridRepository>(), gate, services.GetRequiredService<LimitsOptions>(),
                services.GetRequiredService<ILoggerFactory>(), Ct);

            var problem = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(result);
            Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
            Assert.Equal(Desk.Api.Auth.PrincipalExtensions.NoAccountIdTitle, problem.ProblemDetails.Title);
            Assert.Equal(0, gate.Running);
        }
    }

    private async Task<HttpClient> SignedInOnAsync(WebApplicationFactory<Program> host, string role = Roles.Viewer)
    {
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync(role)).Email!);
        return client;
    }

    [Fact]
    public async Task Refresh_and_logged_out_tokens_draw_on_the_callers_ip_bucket_not_a_users()
    {
        // ADR-0024: a refresh is anonymous until it succeeds, and a token SessionLimits rejects is no user at all,
        // so neither can spend (or hide behind) a signed-in user's bucket.
        await using var host = api.WithSettings(("RATE_LIMIT_PER_USER_PER_MIN", "1"), ("RATE_LIMIT_PER_USER_BURST", "3"));
        var user = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);
        var anonymous = PostgresApiFactory.NewClient(host);
        var tokens = await PostgresApiFactory.LoginAsync(client, user.Email!); // IP bucket: 1 of 3
        Assert.Equal(HttpStatusCode.OK, (await PostgresApiFactory.PostRefreshAsync(anonymous, tokens.RefreshToken)).StatusCode); // 2 of 3
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me", Ct)).StatusCode); // 3 of 3

        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostgresApiFactory.PostRefreshAsync(anonymous, tokens.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);                    // user bucket: 1 of 3
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null, Ct)).StatusCode); // 2 of 3
        // The same token after logout is anonymous, so it lands in the empty IP bucket, not the user's.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/me", Ct)).StatusCode);
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

        var running = client.GetAsync(HoldsAPermit, Ct);   // holds the only permit
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
        var running = client.GetAsync(HoldsAPermit, Ct);   // holds the only permit
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var queued = second.GetAsync(HoldsAPermit, Ct);    // waits in the queue
        await Task.Delay(100, Ct);
        var rejected = await third.GetAsync(HoldsAPermit, Ct);

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

        foreach (var (method, path) in new[] { ("GET", "/api/me"), ("POST", "/api/auth/login"), ("GET", HoldsAPermit), ("GET", "/api/nope") })
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

        // A signed-in client may send its bearer token anywhere: /health, the SPA and Swagger must not read it
        // (the key ring load would wake the database).
        var user = await api.CreateUserAsync(Roles.Admin);
        var token = (await PostgresApiFactory.LoginAsync(api.NewClient(), user.Email!)).AccessToken;
        foreach (var path in new[] { "/health", "/positions", "/swagger/index.html", "/openapi/v1.json" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
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
    public async Task Health_db_is_anonymous_and_says_only_that_the_database_is_up()
    {
        // #113: the same exposure as /health, outside /api, and nothing but up or down.
        var count = new CountingProbes();
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(count)));
        var res = await PostgresApiFactory.NewClient(host).GetAsync("/health/db", Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("""{"status":"up"}""", await res.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, count.Probes);
    }

    [Fact]
    public async Task Health_db_is_503_when_the_database_is_unreachable_and_says_nothing_else()
    {
        await using var broken = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(new FailingCommands())));
        var res = await PostgresApiFactory.NewClient(broken).GetAsync("/health/db", Ct);
        await AssertDownAsync(res);
    }

    /// <summary>Down is a 503 problem with a title and nothing else: no detail, host, timing or version.</summary>
    private static async Task AssertDownAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync(Ct);
        var problem = System.Text.Json.JsonSerializer.Deserialize<ProblemDetails>(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(HealthEndpoints.DownTitle, problem.Title);
        Assert.Null(problem.Detail);
        Assert.DoesNotContain("Host", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_db_checks_the_database_at_most_once_per_interval()
    {
        // Anonymous and outside the rate limits: polling it must not open a connection per request.
        var count = new CountingProbes();
        var (owned, clock) = api.WithOwnClock();
        await using var _ = owned;
        await using var host = owned.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(count)));
        var client = PostgresApiFactory.NewClient(host);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/db", Ct)).StatusCode);
        Assert.Equal(1, count.Probes);

        clock.Advance(DbHealthProbe.CacheFor - TimeSpan.FromMilliseconds(1));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/db", Ct)).StatusCode);
        Assert.Equal(1, count.Probes);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/db", Ct)).StatusCode);
        Assert.Equal(2, count.Probes);
    }

    [Fact]
    public async Task Health_db_in_maintenance_mode_is_down_without_opening_a_connection()
    {
        var count = new CountingProbes();
        await using var host = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("MAINTENANCE_MODE", "true");
            b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(count));
        });
        await AssertDownAsync(await PostgresApiFactory.NewClient(host).GetAsync("/health/db", Ct));
        Assert.Equal(0, count.Probes);
        Assert.Equal(0, api.ConnectionsOpened(host));
    }

    [Fact]
    public async Task The_old_api_health_db_path_is_gone()
    {
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/health/db", Ct)).StatusCode);
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
            ["RATE_LIMIT_PER_USER_INSIGHTS_CONCURRENCY"] = "0",
            ["RATE_LIMIT_PER_USER_INSIGHTS_QUEUE"] = "many",
            ["EXPORT_GLOBAL_SLOTS"] = "x",
            ["EXPORT_TIMEOUT_SECONDS"] = "-1",
        }).Build();
        Assert.Equal(new LimitsOptions(60, 40, 5, 8, 32, 1, 3, 5, 15, 2, TimeSpan.FromSeconds(60)), LimitsOptions.From(config));
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
        await PostgresApiFactory.LoginAsync(client, admin.Email!);

        var running = client.GetAsync(HoldsAPermit, Ct);   // the user's one permit, held in the database
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
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(export, Ct)).StatusCode);
        }
        // However routing lets the path be spelled.
        using (var export = new HttpRequestMessage(HttpMethod.Post, "/API/Positions/Export/") { Content = JsonContent.Create(new { columns = new[] { "deal_name" } }) })
        {
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

        var running = client.GetAsync(HoldsAPermit, Ct);   // A's one permit, and one of the two database permits
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
    public async Task A_request_queued_for_the_shared_database_limit_spends_one_token()
    {
        // #184: the middleware tries a synchronous acquire before it queues. A token spent on that failed attempt is
        // never given back, so a request that waits for a database permit would cost its caller two.
        var gate = new BlockingCommands();
        await using var host = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("RATE_LIMIT_GLOBAL_CONCURRENCY", "1");
            b.UseSetting("RATE_LIMIT_GLOBAL_QUEUE", "1");
            b.UseSetting("RATE_LIMIT_PER_USER_PER_MIN", "1");
            b.UseSetting("RATE_LIMIT_PER_USER_BURST", "3");
            b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(gate));
        });
        var admin = await api.CreateUserAsync(Roles.Admin);
        var holder = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(holder, admin.Email!);
        var user = await SignedInOnAsync(host); // before the only permit is taken: login needs one

        var running = holder.GetAsync(HoldsAPermit, Ct);    // holds the only database permit
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var queued = user.GetAsync("/api/me", Ct);           // waits in the shared queue: token 1 of 3
        await Task.Delay(100, Ct);
        Assert.False(queued.IsCompleted);

        gate.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await queued).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/me", Ct)).StatusCode); // 2 of 3
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/me", Ct)).StatusCode); // 3 of 3
        Assert.Equal(HttpStatusCode.TooManyRequests, (await user.GetAsync("/api/me", Ct)).StatusCode);
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

        var running = client.GetAsync(HoldsAPermit, Ct);   // holds the only database permit
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

        var running = client.GetAsync(HoldsAPermit, Ct);   // holds the user's one permit
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

    private static HttpRequestMessage Export(params string[] columns) =>
        new(HttpMethod.Post, "/api/positions/export") { Content = JsonContent.Create(new { columns }) };

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
            await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);
            var res = await client.SendAsync(Export("deal_name"), Ct);
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
        await PostgresApiFactory.LoginAsync(client, user.Email!);

        var res = await client.SendAsync(Export("deal_name"), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("Export timed out", (await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
        Assert.Null(res.Content.Headers.ContentDisposition); // a problem, not a file
        Assert.False(res.Headers.Contains("Retry-After"));  // the same export would time out again
        var gate = host.Services.GetRequiredService<ExportGate>();
        Assert.False(gate.IsRunning(user.Id.ToString()));
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
        await PostgresApiFactory.LoginAsync(client, user.Email!);
        var gate = host.Services.GetRequiredService<ExportGate>();

        // Headers arrive with the first flushed chunk; then nothing is read, so the server's writes block (backpressure)
        // until the deadline fires.
        using var res = await client.SendAsync(Export(await AllColumnsAsync(client)), HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(gate.IsRunning(user.Id.ToString()));
        await WaitUntilAsync(() => !gate.IsRunning(user.Id.ToString()) && gate.Running == 0);

        // The partial file never ends like a complete one: reading it to the end fails.
        await Assert.ThrowsAnyAsync<Exception>(async () => await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_client_that_goes_away_releases_the_export_slots()
    {
        await using var host = api.WithSettings(("EXPORT_TIMEOUT_SECONDS", "60"));
        var user = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, user.Email!);
        var gate = host.Services.GetRequiredService<ExportGate>();

        var res = await client.SendAsync(Export(await AllColumnsAsync(client)), HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.True(gate.IsRunning(user.Id.ToString()));
        res.Dispose(); // the browser tab closed: the request is aborted while the server is blocked writing
        await WaitUntilAsync(() => !gate.IsRunning(user.Id.ToString()) && gate.Running == 0);

        // And the user can export again straight away.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Export("deal_name"), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_renamed_account_cannot_run_a_second_export_from_its_other_session()
    {
        await using var host = api.WithSettings(("EXPORT_TIMEOUT_SECONDS", "60"));
        var user = await api.CreateUserAsync();
        var (before, after) = await SignedInBeforeAndAfterARenameAsync(host, user);
        var gate = host.Services.GetRequiredService<ExportGate>();

        var res = await before.SendAsync(Export(await AllColumnsAsync(before)), HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.True(gate.IsRunning(user.Id.ToString()));
        var second = await after.SendAsync(Export("deal_name"), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("Export already running", (await second.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);

        res.Dispose();
        await WaitUntilAsync(() => !gate.IsRunning(user.Id.ToString()) && gate.Running == 0);
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

    // ---- #357: insights source reads get their own per-user partition (5 in flight, queue 15) ----

    [Fact]
    public void Insights_partition_defaults_to_5_in_flight_and_a_queue_of_15_and_is_configurable()
    {
        var defaults = LimitsOptions.From(Config());
        Assert.Equal((5, 15), (defaults.InsightsConcurrency, defaults.InsightsQueue));
        Assert.Equal((1, 8), (defaults.PerUserConcurrency, defaults.PerUserQueue)); // everything else is unchanged

        var set = LimitsOptions.From(Config(("RATE_LIMIT_PER_USER_INSIGHTS_CONCURRENCY", "2"), ("RATE_LIMIT_PER_USER_INSIGHTS_QUEUE", "1")));
        Assert.Equal((2, 1), (set.InsightsConcurrency, set.InsightsQueue));
        Assert.Equal((1, 8), (set.PerUserConcurrency, set.PerUserQueue));

        foreach (var bad in new[] { "0", "-1", "five", "" })
        {
            var fallback = LimitsOptions.From(Config(("RATE_LIMIT_PER_USER_INSIGHTS_CONCURRENCY", bad), ("RATE_LIMIT_PER_USER_INSIGHTS_QUEUE", bad)));
            Assert.Equal((5, 15), (fallback.InsightsConcurrency, fallback.InsightsQueue));
        }
    }

    [Fact]
    public void Only_a_routed_insights_endpoint_gets_the_insights_partition()
    {
        var unrouted = new DefaultHttpContext();
        unrouted.Request.Path = "/api/insights/core";
        Assert.False(RateLimiting.IsInsightsSource(unrouted)); // no endpoint matched: the path alone moves nothing

        var other = new DefaultHttpContext();
        other.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(ExportEndpoint.Instance), "export"));
        Assert.False(RateLimiting.IsInsightsSource(other));

        var insights = new DefaultHttpContext();
        insights.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(InsightsSourceEndpoint.Instance), "insights"));
        Assert.True(RateLimiting.IsInsightsSource(insights));
        Assert.False(RateLimiting.IsExport(insights));

        // Of every endpoint the app maps, only GET /api/insights/{source} carries the marker.
        var marked = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<InsightsSourceEndpoint>() is not null)
            .Select(e => e.DisplayName)
            .ToArray();
        var only = Assert.Single(marked);
        Assert.Contains("/api/insights/{source}", only, StringComparison.Ordinal);
    }

    [Fact]
    public void The_deal_endpoints_stay_in_the_callers_main_per_user_partition()
    {
        // List and detail are two calls at most, so they queue behind each other in the main partition (one in flight)
        // rather than getting their own like insights, and neither is an export.
        var deals = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/deals", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(2, deals.Length);
        Assert.All(deals, e =>
        {
            Assert.Null(e.Metadata.GetMetadata<InsightsSourceEndpoint>());
            Assert.Null(e.Metadata.GetMetadata<ExportEndpoint>());
        });
    }

    [Fact]
    public void A_users_insights_partition_key_never_equals_any_callers_main_key()
    {
        var clients = api.Services.GetRequiredService<ClientAddress>();
        // #185: a signed-in caller is keyed on the account id, so the values that must not collide are NameIdentifiers.
        foreach (var id in new[] { "alice@example.com", "insights:u:alice@example.com", "u:alice@example.com" })
        {
            var http = new DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "alice@example.com"),
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, id),
                    ], "test")),
            };
            Assert.Equal($"insights:{RateLimiting.PartitionKey(http, clients)}", RateLimiting.InsightsPartitionKey(http, clients));
            // Main keys start with u: or ip:, so no account id can make one equal to an insights key.
            Assert.StartsWith("u:", RateLimiting.PartitionKey(http, clients), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Holds every connection to one source until released. With <c>grid=</c>, an insights read opens exactly one
    /// connection, so <see cref="Entered"/> counts the reads held in flight (each with its permits).
    /// </summary>
    private sealed class GatedSources(Desk.Data.Sources.DataSourceRegistry inner, string gated) : Desk.Data.Sources.IDataSourceRegistry
    {
        private int _entered;

        public int Entered => Volatile.Read(ref _entered);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Npgsql.NpgsqlDataSource Get(string source) => inner.Get(source);

        public async ValueTask<Npgsql.NpgsqlConnection> OpenAsync(string source, CancellationToken ct)
        {
            if (source == gated)
            {
                Interlocked.Increment(ref _entered);
                await Release.Task.WaitAsync(ct);
            }
            return await inner.OpenAsync(source, ct);
        }
    }

    /// <summary>One surveillance grid: a single connection, held by <see cref="GatedSources"/>.</summary>
    private const string HeldInsights = "/api/insights/surveillance?grid=oc_cushion_buckets";

    private WebApplicationFactory<Program> WithGatedSurveillance(GatedSources[] gate, IInterceptor? commands, params (string Key, string Value)[] settings) =>
        api.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<Desk.Data.Sources.DataSourceRegistry>();
                s.AddSingleton<Desk.Data.Sources.IDataSourceRegistry>(sp => gate[0] = new GatedSources(
                    sp.GetRequiredService<Desk.Data.Sources.DataSourceRegistry>(), ConnectionStrings.Surveillance));
                if (commands is not null)
                    s.AddSingleton(commands);
            });
        });

    /// <summary>Waits until <paramref name="count"/> insights reads are held in the database, each with its permits.</summary>
    private static async Task<GatedSources> HeldAsync(GatedSources[] gate, int count)
    {
        for (var i = 0; i < 400 && (gate[0] is null || gate[0].Entered < count); i++)
            await Task.Delay(25, Ct);
        Assert.NotNull(gate[0]);
        Assert.Equal(count, gate[0].Entered);
        return gate[0];
    }

    [Fact]
    public async Task Insights_reads_have_their_own_in_flight_cap_and_queue_then_429()
    {
        var gate = new GatedSources[1];
        await using var host = WithGatedSurveillance(gate, null,
            ("RATE_LIMIT_PER_USER_INSIGHTS_CONCURRENCY", "2"), ("RATE_LIMIT_PER_USER_INSIGHTS_QUEUE", "1"));
        var client = await SignedInOnAsync(host);
        var other = await SignedInOnAsync(host);

        var running = new[] { client.GetAsync(HeldInsights, Ct), client.GetAsync(HeldInsights, Ct) }; // both permits
        var held = await HeldAsync(gate, 2);
        var queued = client.GetAsync("/api/insights/core", Ct);   // the one queue place
        await Task.Delay(100, Ct);
        var rejected = await client.GetAsync("/api/insights/market", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("1", rejected.Headers.RetryAfter?.ToString());
        Assert.Equal("Too many requests", (await rejected.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
        Assert.False(queued.IsCompleted);

        // A full insights partition leaves the user's other calls alone: they have their own partition.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        // And another user's board isn't behind this one.
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/insights/market", Ct)).StatusCode);

        held.Release.TrySetResult();
        foreach (var r in running)
            Assert.Equal(HttpStatusCode.OK, (await r).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await queued).StatusCode);
        Assert.Equal(2, held.Entered); // the queued core read never touched surveillance
    }

    [Fact]
    public async Task By_default_a_user_has_5_insights_reads_in_flight_and_15_queued_then_429()
    {
        var gate = new GatedSources[1];
        await using var host = WithGatedSurveillance(gate, null);
        var client = await SignedInOnAsync(host);

        var running = Enumerable.Range(0, 5).Select(_ => client.GetAsync(HeldInsights, Ct)).ToArray();
        var held = await HeldAsync(gate, 5);
        var queued = Enumerable.Range(0, 15).Select(_ => client.GetAsync("/api/insights/core", Ct)).ToArray();
        await Task.Delay(200, Ct);
        Assert.All(queued, q => Assert.False(q.IsCompleted));
        var rejected = await client.GetAsync("/api/insights/core", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("1", rejected.Headers.RetryAfter?.ToString());

        held.Release.TrySetResult();
        foreach (var r in running.Concat(queued))
            Assert.Equal(HttpStatusCode.OK, (await r).StatusCode);
    }

    [Fact]
    public async Task With_insights_reads_in_flight_other_routes_still_run_one_at_a_time_with_a_queue_of_8()
    {
        var gate = new GatedSources[1];
        var commands = new BlockingCommands();
        await using var host = WithGatedSurveillance(gate, commands);
        var admin = await api.CreateUserAsync(Roles.Admin);
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, admin.Email!);

        var insights = new[] { client.GetAsync(HeldInsights, Ct), client.GetAsync(HeldInsights, Ct) }; // two insights permits
        var held = await HeldAsync(gate, 2);

        var running = client.GetAsync(HoldsAPermit, Ct);   // the user's one main permit, held in the database
        await commands.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var queued = Enumerable.Range(0, 8).Select(_ => client.GetAsync("/api/me", Ct)).ToArray(); // the 8 queue places
        await Task.Delay(200, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.All(queued, q => Assert.False(q.IsCompleted));

        // The main partition is full, and an insights read still gets one of the three insights permits left.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/insights/pricing", Ct)).StatusCode);

        commands.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        foreach (var q in queued)
            Assert.Equal(HttpStatusCode.OK, (await q).StatusCode);
        held.Release.TrySetResult();
        foreach (var r in insights)
            Assert.Equal(HttpStatusCode.OK, (await r).StatusCode);
    }

    [Fact]
    public async Task An_insights_read_waiting_in_its_users_queue_holds_no_database_permit()
    {
        var gate = new GatedSources[1];
        await using var host = WithGatedSurveillance(gate, null,
            ("RATE_LIMIT_PER_USER_INSIGHTS_CONCURRENCY", "1"), ("RATE_LIMIT_PER_USER_INSIGHTS_QUEUE", "2"),
            ("RATE_LIMIT_GLOBAL_CONCURRENCY", "2"), ("RATE_LIMIT_GLOBAL_QUEUE", "1"));
        var client = await SignedInOnAsync(host);
        var other = await SignedInOnAsync(host); // before any permit is taken: login needs one

        var running = client.GetAsync(HeldInsights, Ct);        // A's one insights permit, and one of the two database permits
        var held = await HeldAsync(gate, 1);
        var queued = new[] { client.GetAsync("/api/insights/core", Ct), client.GetAsync("/api/insights/market", Ct) }; // in A's own queue
        await Task.Delay(100, Ct);

        // Had A's queued reads taken the second database permit and the shared queue place, B would get a 429.
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/insights/reference", Ct)).StatusCode);
        Assert.All(queued, q => Assert.False(q.IsCompleted));

        held.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        foreach (var q in queued)
            Assert.Equal(HttpStatusCode.OK, (await q).StatusCode);
    }

    [Fact]
    public async Task An_insights_read_queued_behind_its_own_user_spends_one_token()
    {
        // No refill during the test: 3 tokens are all this user gets, one per read. With the token bucket before the
        // per-user partition, each queued read would pay twice and the third would be a 429.
        var gate = new GatedSources[1];
        await using var host = WithGatedSurveillance(gate, null,
            ("RATE_LIMIT_PER_USER_INSIGHTS_CONCURRENCY", "1"),
            ("RATE_LIMIT_PER_USER_PER_MIN", "1"), ("RATE_LIMIT_PER_USER_BURST", "3"));
        var client = await SignedInOnAsync(host);

        var running = client.GetAsync(HeldInsights, Ct);
        var held = await HeldAsync(gate, 1);
        var second = client.GetAsync("/api/insights/core", Ct);
        var third = client.GetAsync("/api/insights/market", Ct);
        await Task.Delay(100, Ct);

        held.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await third).StatusCode);
    }

    /// <summary>A signed-in request that reads the database through the /api limiters (a user's saved presets).</summary>
    private const string HoldsAPermit = "/api/presets/positions";

    /// <summary>Holds the <see cref="HoldsAPermit"/> query until released, so the test controls how long a permit is held.</summary>
    private sealed class BlockingCommands : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("preset", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    /// <summary>Counts the database probes (<c>SELECT 1</c>) that reach the database.</summary>
    private sealed class CountingProbes : DbCommandInterceptor
    {
        private int _probes;
        public int Probes => Volatile.Read(ref _probes);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText == "SELECT 1")
                Interlocked.Increment(ref _probes);
            return ValueTask.FromResult(result);
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
