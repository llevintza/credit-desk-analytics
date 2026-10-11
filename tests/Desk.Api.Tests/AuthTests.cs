using System.Net;
using System.Net.Http.Json;
using Desk.Api.Auth;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Desk.Api.Tests;

/// <summary>README §11 auth list and §7.1 session rules, on bearer tokens (ADR-0024).</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_token_is_401_problem_details()
    {
        var res = await api.NewClient().GetAsync("/api/me", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_returns_bearer_tokens_and_no_cookies_and_me_returns_the_account()
    {
        var user = await api.CreateUserAsync(Roles.Admin);
        var client = api.NewClient();

        var login = await PostgresApiFactory.PostLoginAsync(client, user.Email!);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(login.Headers.Contains("Set-Cookie"));
        var tokens = (await login.Content.ReadFromJsonAsync<AccessTokenResponse>(Ct))!;
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.Equal((long)AuthSetup.AccessTokenLifetime.TotalSeconds, tokens.ExpiresIn);
        Assert.False(string.IsNullOrEmpty(tokens.AccessToken));
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));
        Assert.NotEqual(tokens.AccessToken, tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        PostgresApiFactory.UseToken(client, tokens.AccessToken);
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
        await PostgresApiFactory.LoginAsync(client, user.Email!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("Bearer not-a-token")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    public async Task A_bad_or_foreign_authorization_header_is_401(string header)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        req.Headers.TryAddWithoutValidation("Authorization", header);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().SendAsync(req, Ct)).StatusCode);
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
        // Stamped from the injected clock, not the wall clock (#261).
        Assert.Equal(api.Time.GetUtcNow() + IdentityPolicy.LockoutDuration, stored.LockoutEnd);
        Assert.Equal(0, stored.AccessFailedCount);
    }

    [Fact]
    public async Task Lockout_ends_on_the_injected_clock()
    {
        var (host, clock) = api.WithOwnClock();
        await using var _ = host;
        var user = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);
        for (var i = 0; i < IdentityPolicy.MaxFailedAttempts; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostLoginAsync(client, user.Email!, "wrong-password-123456")).StatusCode);

        clock.Advance(IdentityPolicy.LockoutDuration - TimeSpan.FromTicks(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostLoginAsync(client, user.Email!)).StatusCode);
        // Identity's boundary is inclusive: still locked at the instant the lockout ends.
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostLoginAsync(client, user.Email!)).StatusCode);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(HttpStatusCode.OK, (await PostgresApiFactory.PostLoginAsync(client, user.Email!)).StatusCode);
    }

    [Fact]
    public async Task Lockout_end_is_ignored_when_lockout_is_off_for_the_account()
    {
        var user = await api.CreateUserAsync();
        await using var scope = api.Services.CreateAsyncScope();
        var users = Assert.IsType<DeskUserManager>(scope.ServiceProvider.GetRequiredService<UserManager<DeskUser>>());
        var tracked = (await users.FindByIdAsync(user.Id.ToString()))!;
        Assert.True((await users.SetLockoutEndDateAsync(tracked, api.Time.GetUtcNow().AddDays(1))).Succeeded);
        Assert.True(await users.IsLockedOutAsync(tracked));

        Assert.True((await users.SetLockoutEnabledAsync(tracked, false)).Succeeded);
        Assert.False(await users.IsLockedOutAsync(tracked));
    }

    [Fact]
    public async Task The_access_token_lasts_5_minutes_and_a_refresh_issues_a_new_pair()
    {
        var (host, clock) = api.WithOwnClock();
        await using var _ = host;
        var (client, tokens) = await SignedInAsync(host);

        clock.Advance(AuthSetup.AccessTokenLifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);

        var fresh = await RefreshAsync(client, tokens.RefreshToken);
        Assert.Equal("Bearer", fresh.TokenType);
        Assert.NotEqual(tokens.AccessToken, fresh.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Refresh_slides_the_session_for_8_hours_of_idle_time()
    {
        var (host, clock) = api.WithOwnClock();
        await using var _ = host;
        var (client, tokens) = await SignedInAsync(host);

        clock.Advance(AuthSetup.SlidingExpiry - TimeSpan.FromMinutes(1));
        var renewed = await RefreshAsync(client, tokens.RefreshToken);

        // The renewed refresh token starts its own 8 h; the first one has run out.
        clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(client, tokens.RefreshToken)).StatusCode);
        renewed = await RefreshAsync(client, renewed.RefreshToken);

        clock.Advance(AuthSetup.SlidingExpiry);
        var idle = await PostgresApiFactory.PostRefreshAsync(client, renewed.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, idle.StatusCode);
        Assert.Equal(AuthEndpoints.RefreshFailedDetail, (await idle.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail);
    }

    [Fact]
    public async Task Session_ends_after_24_hours_even_when_refreshed()
    {
        var (host, clock) = api.WithOwnClock();
        await using var _ = host;
        var (client, tokens) = await SignedInAsync(host);
        // Refresh every 4 h: the sliding limit alone would keep the session alive forever.
        var refresh = tokens.RefreshToken;
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromHours(4));
            refresh = (await RefreshAsync(client, refresh)).RefreshToken;
        }

        // 20 h in. A token issued at 23 h 59 min still stops at the 24 h mark, before its own 5 minutes are up.
        clock.Advance(TimeSpan.FromHours(4) - TimeSpan.FromMinutes(1));
        refresh = (await RefreshAsync(client, refresh)).RefreshToken;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(client, refresh)).StatusCode);
    }

    [Fact]
    public async Task Session_ends_on_every_request_when_the_account_expires()
    {
        var (host, clock) = api.WithOwnClock();
        await using var _ = host;
        var user = await api.CreateUserAsync(expiresAt: clock.GetUtcNow().AddMinutes(2));
        var client = PostgresApiFactory.NewClient(host);
        var tokens = await PostgresApiFactory.LoginAsync(client, user.Email!);

        // The access token has 3 of its 5 minutes left, but the account is over.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(client, tokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Refresh_reads_the_expiry_from_the_database_not_the_token()
    {
        var (host, clock) = api.WithOwnClock();
        await using var _ = host;
        var user = await api.CreateUserAsync(expiresAt: clock.GetUtcNow().AddDays(1));
        var client = PostgresApiFactory.NewClient(host);
        var tokens = await PostgresApiFactory.LoginAsync(client, user.Email!);
        // An admin shortens the account (Desk.UserAdmin) to the past; the token still says tomorrow.
        await using (var db = api.NewContext())
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.ExpiresAt, clock.GetUtcNow().AddMinutes(-1)), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(client, tokens.RefreshToken)).StatusCode);
    }

    [Theory]
    [InlineData(true)]  // the admin CLI also rotates the stamp
    [InlineData(false)] // the flag alone: the refresh still refuses an account that can't sign in
    public async Task Disabling_ends_the_session_at_the_next_refresh(bool rotateStamp)
    {
        var (host, clock) = api.WithOwnClock();
        await using var _ = host;
        var (client, tokens, user) = await SignedInWithUserAsync(host);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<DeskUser>>();
            var tracked = (await users.FindByIdAsync(user.Id.ToString()))!;
            tracked.IsDisabled = true;
            await users.UpdateAsync(tracked);
            if (rotateStamp) await users.UpdateSecurityStampAsync(tracked);
        }

        // Limit (ADR-0024): the access token already issued runs out its 5 minutes; the refresh is refused.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        clock.Advance(AuthSetup.AccessTokenLifetime);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(client, tokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task A_password_reset_ends_every_session_at_the_next_refresh()
    {
        var (host, _) = api.WithOwnClock();
        await using var __ = host;
        var (client, tokens, user) = await SignedInWithUserAsync(host);
        var other = PostgresApiFactory.NewClient(host);
        var otherTokens = await PostgresApiFactory.LoginAsync(other, user.Email!);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<DeskUser>>();
            var tracked = (await users.FindByIdAsync(user.Id.ToString()))!;
            // As `Desk.UserAdmin reset` does it: a new hash and a new stamp in one write.
            tracked.PasswordHash = users.PasswordHasher.HashPassword(tracked, PasswordGenerator.Generate());
            Assert.True((await users.UpdateSecurityStampAsync(tracked)).Succeeded);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(client, tokens.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(other, otherTokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task A_lockout_does_not_end_a_live_session()
    {
        // Lockout counts failed passwords, which anyone can send: it must not let a stranger sign a user out.
        var (client, tokens, user) = await api.SignedInAsync();
        var stranger = api.NewClient();
        for (var i = 0; i < IdentityPolicy.MaxFailedAttempts; i++)
            await PostgresApiFactory.PostLoginAsync(stranger, user.Email!, "wrong-password-123456");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostgresApiFactory.PostRefreshAsync(client, tokens.RefreshToken)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task Refresh_refuses_a_missing_or_garbage_token(string? token)
    {
        var res = await PostgresApiFactory.PostRefreshAsync(api.NewClient(), token);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(AuthEndpoints.RefreshFailedDetail, (await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail);
    }

    [Fact]
    public async Task Tokens_are_not_interchangeable()
    {
        var (client, tokens, _) = await api.SignedInAsync();
        // Different data-protection purposes: an access token is no refresh token, and the reverse.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(client, tokens.AccessToken)).StatusCode);
        PostgresApiFactory.UseToken(client, tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Refresh_keeps_the_roles_current_and_the_sign_in_identity()
    {
        var (client, tokens, user) = await api.SignedInAsync();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<DeskUser>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(user.Id.ToString()))!, Roles.Admin)).Succeeded);
        }

        await RefreshAsync(client, tokens.RefreshToken);
        var me = await client.GetFromJsonAsync<MeResponse>("/api/me", Ct);
        Assert.Equal(user.Email, me!.Email);
        Assert.Equal([Roles.Admin, Roles.Viewer], me.Roles);
    }

    [Fact]
    public async Task Logout_ends_this_sign_in_and_only_this_one()
    {
        var (client, tokens, user) = await api.SignedInAsync();
        var other = api.NewClient();
        var otherTokens = await PostgresApiFactory.LoginAsync(other, user.Email!);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(client, tokens.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostgresApiFactory.PostRefreshAsync(other, otherTokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Logout_ends_tokens_refreshed_from_the_same_sign_in()
    {
        var (client, tokens, _) = await api.SignedInAsync();
        var renewed = await RefreshAsync(client, tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(client, renewed.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(client, tokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Logout_needs_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().PostAsync("/api/auth/logout", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Data_protection_keys_are_persisted_so_tokens_survive_a_restart()
    {
        var (_, tokens, _) = await api.SignedInAsync();
        await using (var db = api.NewContext())
            Assert.True(await db.DataProtectionKeys.AnyAsync(Ct));

        // A second host on the same database stands in for a restarted instance: it reads the first one's tokens.
        await using var restarted = api.WithSettings();
        var client = PostgresApiFactory.NewClient(restarted);
        PostgresApiFactory.UseToken(client, tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostgresApiFactory.PostRefreshAsync(client, tokens.RefreshToken)).StatusCode);
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

    [Fact]
    public void Shared_fixture_clock_reads_the_fixed_start_instant()
    {
        // #226: no test may move the shared clock (PostgresApiFactory.DisposeAsync checks it again after every test ran).
        Assert.Equal(PostgresApiFactory.Start, api.Time.GetUtcNow());
    }

    [Fact]
    public void Shared_fixture_clock_cannot_be_cast_to_the_fake()
    {
        // #262: neither the fixture's clock nor the host's TimeProvider is a FakeTimeProvider anyone could Advance.
        Assert.Throws<InvalidCastException>(() => (FakeTimeProvider)api.Time);
        Assert.Throws<InvalidCastException>(() => (FakeTimeProvider)api.Services.GetRequiredService<TimeProvider>());
        Assert.Same(api.Time, api.Services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Shared_fixture_clock_forwards_to_a_clock_that_never_moves()
    {
        var time = api.Time;
        var reference = new FakeTimeProvider(PostgresApiFactory.Start);
        Assert.Equal(reference.TimestampFrequency, time.TimestampFrequency);
        Assert.Equal(reference.LocalTimeZone, time.LocalTimeZone);
        var before = time.GetTimestamp();
        var fired = false;
        using (time.CreateTimer(_ => fired = true, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan))
            Assert.True(fired); // a fake timer due now fires on creation; it can't move the clock
        Assert.Equal(before, time.GetTimestamp());
        Assert.Equal(PostgresApiFactory.Start, time.GetUtcNow());
    }

    [Fact]
    public async Task Fixture_teardown_reports_a_moved_clock_even_when_a_disposal_throws()
    {
        // #262: the clock is read before teardown and every disposal still runs, so neither failure hides the other.
        var clock = new FakeTimeProvider(PostgresApiFactory.Start);
        clock.Advance(TimeSpan.FromMinutes(1));
        var ran = new List<string>();
        var error = await Assert.ThrowsAsync<AggregateException>(async () => await PostgresApiFactory.TeardownAsync(clock,
            () => { ran.Add("host"); throw new IOException("host teardown failed"); },
            () => { ran.Add("db"); clock.Advance(TimeSpan.FromMinutes(1)); return ValueTask.CompletedTask; }));
        Assert.Equal(["host", "db"], ran);
        Assert.Collection(error.InnerExceptions,
            e => Assert.Contains("moved the shared api-postgres clock to 2026-10-07T12:01:00", Assert.IsType<InvalidOperationException>(e).Message),
            e => Assert.Equal("host teardown failed", Assert.IsType<IOException>(e).Message));
    }

    [Fact]
    public async Task Fixture_teardown_passes_on_an_unmoved_clock_and_reports_teardown_errors_alone()
    {
        var clock = new FakeTimeProvider(PostgresApiFactory.Start);
        var ran = 0;
        await PostgresApiFactory.TeardownAsync(clock, () => { ran++; return ValueTask.CompletedTask; });
        Assert.Equal(1, ran);
        var error = await Assert.ThrowsAsync<AggregateException>(async () => await PostgresApiFactory.TeardownAsync(clock,
            () => throw new IOException("db teardown failed")));
        Assert.IsType<IOException>(Assert.Single(error.InnerExceptions));
    }

    [Fact]
    public async Task Own_clock_hosts_leave_the_shared_clock_where_it_was()
    {
        var (host, clock) = api.WithOwnClock();
        await using var _ = host;
        Assert.Equal(PostgresApiFactory.Start, clock.GetUtcNow());
        var (client, _) = await SignedInAsync(host);

        clock.Advance(AuthSetup.AbsoluteExpiry + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(PostgresApiFactory.Start, api.Time.GetUtcNow());
    }

    private async Task<(HttpClient Client, AccessTokenResponse Tokens)> SignedInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var (client, tokens, _) = await SignedInWithUserAsync(host);
        return (client, tokens);
    }

    private async Task<(HttpClient Client, AccessTokenResponse Tokens, DeskUser User)> SignedInWithUserAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var user = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);
        return (client, await PostgresApiFactory.LoginAsync(client, user.Email!), user);
    }

    /// <summary>Refreshes, asserts success, and sends the new access token from <paramref name="client"/> from now on.</summary>
    private static async Task<AccessTokenResponse> RefreshAsync(HttpClient client, string refreshToken)
    {
        var res = await PostgresApiFactory.PostRefreshAsync(client, refreshToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var tokens = (await res.Content.ReadFromJsonAsync<AccessTokenResponse>(Ct))!;
        PostgresApiFactory.UseToken(client, tokens.AccessToken);
        return tokens;
    }
}
