using System.Net;
using System.Net.Http.Json;
using Desk.Api.Auth;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Api.Tests;

/// <summary>README §11 auth list and §7.1 session rules.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_session_is_401_problem_details()
    {
        var res = await api.NewClient().GetAsync("/api/me", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_sets_a_strict_host_only_session_cookie_and_me_returns_the_account()
    {
        var user = await api.CreateUserAsync(Roles.Admin);
        var client = api.NewClient();

        var login = await PostgresApiFactory.PostLoginAsync(client, user.Email!);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthSetup.CookieName + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", session, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", session, StringComparison.OrdinalIgnoreCase);
        // Readable by the SPA (no HttpOnly), still Secure + Strict.
        var xsrf = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthSetup.XsrfCookieName + "=", StringComparison.Ordinal));
        Assert.DoesNotContain("httponly", xsrf, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", xsrf, StringComparison.OrdinalIgnoreCase);

        var body = await login.Content.ReadFromJsonAsync<MeResponse>(Ct);
        Assert.Equal(user.Email, body!.Email);

        var me = await client.GetFromJsonAsync<MeResponse>("/api/me", Ct);
        Assert.Equal(user.Email, me!.Email);
        Assert.Equal([Roles.Admin], me.Roles);
        Assert.Equal(user.ExpiresAt, me.ExpiresAt);
    }

    [Fact]
    public async Task Login_works_over_plain_http_for_local_compose_and_the_dev_proxy()
    {
        var user = await api.CreateUserAsync();
        var client = api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        var res = await PostgresApiFactory.PostLoginAsync(client, user.Email!);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotNull(PostgresApiFactory.XsrfToken(res));
    }

    [Fact]
    public async Task Behind_the_proxy_a_forwarded_https_request_is_https()
    {
        // Render terminates TLS; render.yaml sets ASPNETCORE_FORWARDEDHEADERS_ENABLED so the app sees https.
        await using var host = api.WithSettings(("FORWARDEDHEADERS_ENABLED", "true")); // = ASPNETCORE_FORWARDEDHEADERS_ENABLED
        var client = host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://desk.example.test") });
        using var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("X-Forwarded-Proto", "https");
        var res = await client.SendAsync(req, Ct);
        Assert.True(res.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Behind_the_proxy_the_antiforgery_cookie_is_host_prefixed_and_always_secure()
    {
        // Render: the proxy sends X-Forwarded-Proto: https. Cookies are carried by hand, as a browser would on https.
        await using var host = api.WithSettings(("FORWARDEDHEADERS_ENABLED", "true"));
        var client = host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("http://desk.example.test"), HandleCookies = false, AllowAutoRedirect = false });
        var user = await api.CreateUserAsync();

        using var login = Forwarded(HttpMethod.Post, "/api/auth/login");
        login.Content = JsonContent.Create(new LoginRequest(user.Email, PostgresApiFactory.Password));
        var res = await client.SendAsync(login, Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var af = SetCookie(res, AuthSetup.AntiforgeryCookieName);
        Assert.Contains("secure", af, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", af, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", af, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", af, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", af, StringComparison.OrdinalIgnoreCase);
        Assert.Null(SetCookieOrNull(res, AuthSetup.PlainHttpAntiforgeryCookieName));

        // The token validates against the __Host- cookie.
        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(client, res, forwardedHttps: true)).StatusCode);
    }

    [Fact]
    public async Task Off_the_proxy_plain_http_keeps_the_plain_antiforgery_cookie_and_it_validates()
    {
        // Local compose and the CI e2e/budgets stacks: Production over plain http://localhost:8080, no proxy setting.
        var client = api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("http://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        var user = await api.CreateUserAsync();

        var res = await PostgresApiFactory.PostLoginAsync(client, user.Email!);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var af = SetCookie(res, AuthSetup.PlainHttpAntiforgeryCookieName);
        Assert.DoesNotContain("secure", af, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", af, StringComparison.OrdinalIgnoreCase);
        Assert.Null(SetCookieOrNull(res, AuthSetup.AntiforgeryCookieName));

        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(client, res, forwardedHttps: false)).StatusCode);
    }

    [Theory]
    [InlineData("Production", "true", true)]   // Render
    [InlineData("Production", null, false)]    // local compose, CI e2e and budgets
    [InlineData("Production", "false", false)]
    [InlineData("Development", "true", false)] // the dev proxy serves plain HTTP
    [InlineData("Development", null, false)]
    public void The_https_only_antiforgery_cookie_is_for_the_proxy_outside_development(string environment, string? forwarded, bool httpsOnly)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new("FORWARDEDHEADERS_ENABLED", forwarded)]).Build();
        var env = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment };
        Assert.Equal(httpsOnly, AuthSetup.HttpsOnly(config, env));
    }

    private static HttpRequestMessage Forwarded(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("X-Forwarded-Proto", "https");
        return req;
    }

    private static string? SetCookieOrNull(HttpResponseMessage res, string name) =>
        res.Headers.GetValues("Set-Cookie").SingleOrDefault(c => c.StartsWith(name + "=", StringComparison.Ordinal));

    private static string SetCookie(HttpResponseMessage res, string name) =>
        SetCookieOrNull(res, name) ?? throw new Xunit.Sdk.XunitException($"No {name} cookie was set.");

    /// <summary>Sends every cookie the login set, plus the XSRF token in the header, to the logout endpoint.</summary>
    private static Task<HttpResponseMessage> LogoutAsync(HttpClient client, HttpResponseMessage login, bool forwardedHttps)
    {
        var req = forwardedHttps ? Forwarded(HttpMethod.Post, "/api/auth/logout") : new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        req.Headers.Add("Cookie", string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
        req.Headers.Add(AuthSetup.AntiforgeryHeaderName, PostgresApiFactory.XsrfToken(login));
        return client.SendAsync(req, Ct);
    }

    [Theory]
    [InlineData("wrong-password-123456")]
    [InlineData("")]
    public async Task Wrong_or_empty_password_is_401_with_a_generic_message(string password)
    {
        var user = await api.CreateUserAsync();
        var res = await PostgresApiFactory.PostLoginAsync(api.NewClient(), user.Email!, password);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Equal(AuthEndpoints.LoginFailedDetail, problem!.Detail);
    }

    [Theory]
    [InlineData("nobody@example.com")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Unknown_or_missing_email_is_the_same_401(string? email)
    {
        var res = await api.NewClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, PostgresApiFactory.Password), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(AuthEndpoints.LoginFailedDetail, (await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail);
    }

    [Fact]
    public async Task Missing_password_is_the_same_401()
    {
        var user = await api.CreateUserAsync();
        var res = await api.NewClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, null), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Expired_account_is_refused()
    {
        var user = await api.CreateUserAsync(expiresAt: api.Time.GetUtcNow().AddMinutes(-1));
        var res = await PostgresApiFactory.PostLoginAsync(api.NewClient(), user.Email!);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Disabled_account_is_refused()
    {
        var user = await api.CreateUserAsync();
        await using (var db = api.NewContext())
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDisabled, true), Ct);

        var res = await PostgresApiFactory.PostLoginAsync(api.NewClient(), user.Email!);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Five_failures_lock_the_account_even_for_the_right_password()
    {
        var user = await api.CreateUserAsync();
        var client = api.NewClient();
        for (var i = 0; i < IdentityPolicy.MaxFailedAttempts; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostLoginAsync(client, user.Email!, "wrong-password-123456")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostLoginAsync(client, user.Email!)).StatusCode);

        await using var db = api.NewContext();
        var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, Ct);
        Assert.NotNull(stored.LockoutEnd);
        // Identity stamps the lockout from its own clock; either way it lasts 15 minutes.
        Assert.True(stored.LockoutEnd > DateTimeOffset.UtcNow.AddMinutes(14) || stored.LockoutEnd > api.Time.GetUtcNow().AddMinutes(14));
    }

    [Fact]
    public async Task Session_slides_for_8_hours_of_idle_time()
    {
        var (client, _, _) = await api.SignedInAsync();

        api.Time.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);

        api.Time.Advance(TimeSpan.FromHours(8) + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Session_ends_after_24_hours_even_when_active()
    {
        var (client, _, _) = await api.SignedInAsync();
        // Stay active every 4 h: sliding expiry alone would keep the session alive forever.
        for (var i = 0; i < 5; i++)
        {
            api.Time.Advance(TimeSpan.FromHours(4));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        }

        api.Time.Advance(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Session_ends_when_the_account_expires()
    {
        var user = await api.CreateUserAsync(expiresAt: api.Time.GetUtcNow().AddHours(1));
        var client = api.NewClient();
        await PostgresApiFactory.LoginAsync(client, user.Email!);

        api.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Disabling_ends_a_live_session_at_the_next_stamp_check()
    {
        var (client, _, user) = await api.SignedInAsync();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<DeskUser>>();
            var tracked = await users.FindByIdAsync(user.Id.ToString());
            tracked!.IsDisabled = true;
            await users.UpdateAsync(tracked);
            await users.UpdateSecurityStampAsync(tracked);
        }

        api.Time.Advance(AuthSetup.SecurityStampInterval + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Logout_needs_the_antiforgery_token()
    {
        var (client, xsrf, _) = await api.SignedInAsync();

        var without = await client.PostAsync("/api/auth/logout", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);

        using var forged = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        forged.Headers.Add(AuthSetup.AntiforgeryHeaderName, "not-a-token");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(forged, Ct)).StatusCode);

        using var ok = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        ok.Headers.Add(AuthSetup.AntiforgeryHeaderName, xsrf);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(ok, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Antiforgery_endpoint_refreshes_the_token_for_a_session()
    {
        var (client, _, _) = await api.SignedInAsync();
        var res = await client.GetAsync("/api/auth/antiforgery", Ct);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var fresh = PostgresApiFactory.XsrfToken(res);
        Assert.False(string.IsNullOrEmpty(fresh));

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add(AuthSetup.AntiforgeryHeaderName, fresh);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout, Ct)).StatusCode);
    }

    [Fact]
    public async Task Antiforgery_endpoint_needs_a_session()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().GetAsync("/api/auth/antiforgery", Ct)).StatusCode);
    }

    [Fact]
    public async Task Data_protection_keys_are_persisted_so_sessions_survive_a_restart()
    {
        var user = await api.CreateUserAsync();
        var login = await PostgresApiFactory.PostLoginAsync(api.NewClient(), user.Email!);
        var sessionCookie = login.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(AuthSetup.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
        await using (var db = api.NewContext())
            Assert.True(await db.DataProtectionKeys.AnyAsync(Ct));

        // A second host on the same database stands in for a restarted instance: it reads the first one's cookie.
        await using var restarted = api.WithSettings();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        req.Headers.Add("Cookie", sessionCookie);
        Assert.Equal(HttpStatusCode.OK, (await PostgresApiFactory.NewClient(restarted).SendAsync(req, Ct)).StatusCode);
    }

    [Fact]
    public async Task Logins_are_audited_success_and_failure()
    {
        var user = await api.CreateUserAsync();
        var client = api.NewClient();
        await PostgresApiFactory.PostLoginAsync(client, user.Email!, "wrong-password-123456");
        await PostgresApiFactory.LoginAsync(client, user.Email!);

        var rows = await api.WaitForAuditAsync(a => a.UserName == user.Email && a.Kind != AuditKinds.Request, atLeast: 2);
        Assert.Contains(rows, r => r.Kind == AuditKinds.LoginFailure && r.Status == 401);
        Assert.Contains(rows, r => r.Kind == AuditKinds.LoginSuccess && r.Status == 200);
    }

    [Fact]
    public async Task Authenticated_requests_are_audited_with_the_route_pattern()
    {
        var (client, _, user) = await api.SignedInAsync();
        await client.GetAsync("/api/me", Ct);

        var rows = await api.WaitForAuditAsync(a => a.UserName == user.Email && a.Kind == AuditKinds.Request);
        var row = Assert.Single(rows);
        Assert.Equal("GET /api/me", row.Endpoint);
        Assert.Equal(200, row.Status);
        Assert.True(row.Ms >= 0);
    }
}
