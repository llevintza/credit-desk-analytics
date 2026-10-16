using System.Security.Claims;
using System.Threading.RateLimiting;
using Desk.Api.Audit;
using Desk.Api.Auth;
using Desk.Api.Limits;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Desk.Api.Tests;

/// <summary>Edge cases of the request pipeline pieces that a real client can't easily reach.</summary>
public sealed class PipelineUnitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal SignedIn(string name = "u@example.com") =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test"));

    [Fact]
    public async Task Audit_records_a_thrown_request_as_500_and_rethrows()
    {
        var queue = new AuditQueue();
        var mw = new AuditMiddleware(_ => throw new InvalidOperationException("boom"), queue, TimeProvider.System);
        var http = new DefaultHttpContext { User = SignedIn() };
        http.Request.Path = "/api/positions/query";
        http.Request.Method = "POST";

        await Assert.ThrowsAsync<InvalidOperationException>(() => mw.InvokeAsync(http));
        Assert.True(queue.Reader.TryRead(out var e));
        Assert.Equal(500, e!.Status);
        Assert.Equal("POST /api/positions/query", e.Endpoint); // no route matched: falls back to the path
        Assert.Equal(AuditKinds.Request, e.Kind);
    }

    [Fact]
    public async Task Audit_skips_anonymous_requests_and_non_api_paths()
    {
        var queue = new AuditQueue();
        var mw = new AuditMiddleware(_ => Task.CompletedTask, queue, TimeProvider.System);

        var anon = new DefaultHttpContext();
        anon.Request.Path = "/api/me";
        await mw.InvokeAsync(anon);

        var page = new DefaultHttpContext { User = SignedIn() };
        page.Request.Path = "/positions";
        await mw.InvokeAsync(page);

        Assert.False(queue.Reader.TryRead(out _));
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal Session(string? expiresAt = "2026-11-01T00:00:00.0000000+00:00",
        string? authTime = "2026-10-07T11:00:00.0000000+00:00", string? sid = "abc", string? accountId = "0b8f2c4e-1d3a-4f5b-9c6d-7e8f9a0b1c2d")
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "u@example.com")], "test");
        if (accountId is not null) identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, accountId));
        if (expiresAt is not null) identity.AddClaim(new Claim(DeskClaimsFactory.ExpiresAtClaim, expiresAt));
        if (authTime is not null) identity.AddClaim(new Claim(AuthSetup.AuthTimeClaim, authTime));
        if (sid is not null) identity.AddClaim(new Claim(AuthSetup.SessionIdClaim, sid));
        return new ClaimsPrincipal(identity);
    }

    public static TheoryData<string?, string?, string?> DeadSessions => new()
    {
        { null, "2026-10-07T11:00:00.0000000+00:00", "abc" },                               // no expiry claim
        { "2026-10-07T12:00:00.0000000+00:00", "2026-10-07T11:00:00.0000000+00:00", "abc" }, // account expires now
        { "2026-11-01T00:00:00.0000000+00:00", null, "abc" },                               // no sign-in time
        { "2026-11-01T00:00:00.0000000+00:00", "not-a-time", "abc" },                       // tampered or garbage
        { "2026-11-01T00:00:00.0000000+00:00", "2026-10-06T12:00:00.0000000+00:00", "abc" }, // exactly 24 h old
        { "2026-11-01T00:00:00.0000000+00:00", "2026-10-07T11:00:00.0000000+00:00", null },  // no session id
        { "2026-11-01T00:00:00.0000000+00:00", "2026-10-07T11:00:00.0000000+00:00", "" },    // empty session id
    };

    [Theory]
    [MemberData(nameof(DeadSessions))]
    public void A_token_without_a_live_expiry_sign_in_time_or_session_id_is_not_live(string? expiresAt, string? authTime, string? sid)
    {
        Assert.False(SessionLimits.IsLive(Session(expiresAt, authTime, sid), Now, new SessionRevocations()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_token_without_an_account_id_is_not_live(string? accountId)
    {
        // #399 (from the #396 review): defence in depth behind the limiter's #361 refusal; also covers refresh.
        Assert.False(SessionLimits.IsLive(Session(accountId: accountId), Now, new SessionRevocations()));
    }

    [Fact]
    public async Task Session_limits_refuse_a_token_without_an_account_id_before_any_limiter()
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var reached = false;
        var limits = new SessionLimits(_ => { reached = true; return Task.CompletedTask; }, new SessionRevocations(),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now));

        var http = new DefaultHttpContext { User = Session(accountId: null), RequestServices = services };
        http.Response.Body = new MemoryStream();
        await limits.InvokeAsync(http);
        Assert.False(reached); // the limiter never sees it, so it is never keyed on the client address (#361)
        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.False(http.Response.Headers.ContainsKey("Retry-After"));
        http.Response.Body.Position = 0;
        Assert.Contains(Desk.Api.Auth.PrincipalExtensions.NoAccountIdTitle, await new StreamReader(http.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken));

        var live = new DefaultHttpContext { User = Session(), RequestServices = services };
        await limits.InvokeAsync(live);
        Assert.True(reached);
        Assert.True(live.User.IsSignedIn());
    }

    [Fact]
    public void A_token_is_live_until_its_session_is_logged_out()
    {
        var revoked = new SessionRevocations();
        Assert.True(SessionLimits.IsLive(Session(), Now, revoked));
        revoked.Revoke("abc", Now.AddHours(1), Now);
        Assert.False(SessionLimits.IsLive(Session(), Now, revoked));
        Assert.True(SessionLimits.IsLive(Session(sid: "other"), Now, revoked));
    }

    [Fact]
    public void Revocations_expire_and_are_pruned_on_the_next_logout()
    {
        var revoked = new SessionRevocations();
        revoked.Revoke("a", Now.AddMinutes(10), Now);
        revoked.Revoke("past", Now, Now); // already past the 24 h limit: nothing to remember
        Assert.Equal(1, revoked.Count);
        Assert.True(revoked.IsRevoked("a", Now.AddMinutes(9)));
        Assert.False(revoked.IsRevoked("a", Now.AddMinutes(10)));

        revoked.Revoke("b", Now.AddHours(2), Now.AddMinutes(10));
        Assert.False(revoked.IsRevoked("a", Now));
        Assert.Equal(1, revoked.Count);
    }

    [Fact]
    public async Task Session_limits_turn_a_dead_token_into_an_anonymous_caller_and_keep_a_live_one()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now);
        var mw = new SessionLimits(_ => Task.CompletedTask, new SessionRevocations(), time);

        var live = new DefaultHttpContext { User = Session() };
        await mw.InvokeAsync(live);
        Assert.True(live.User.IsSignedIn());

        var dead = new DefaultHttpContext { User = Session(sid: null) };
        await mw.InvokeAsync(dead);
        Assert.False(dead.User.IsSignedIn());

        var anonymous = new DefaultHttpContext();
        await mw.InvokeAsync(anonymous);
        Assert.False(anonymous.User.IsSignedIn());
    }

    [Fact]
    public async Task The_audit_writer_stops_when_the_queue_completes()
    {
        var queue = new AuditQueue();
        var writer = new AuditWriter(queue, new ServiceCollection().BuildServiceProvider(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), AuditTests.Retention(), NullLogger<AuditWriter>.Instance);
        await writer.StartAsync(Ct);
        queue.Complete();
        await writer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(writer.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void IsSignedIn_needs_an_authenticated_identity()
    {
        Assert.True(SignedIn().IsSignedIn());
        Assert.False(new ClaimsPrincipal(new ClaimsIdentity()).IsSignedIn());
        Assert.False(new ClaimsPrincipal().IsSignedIn());
    }

    [Fact]
    public void The_grace_window_falls_back_to_30_seconds_and_is_clamped_to_2_minutes()
    {
        static TimeSpan Grace(string? value) => SessionStore.GraceFrom(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SessionStore.GraceSecondsKey] = value }).Build());

        foreach (var bad in new[] { null, "", "0", "-5", "soon", "NaN", "Infinity", "-Infinity" })
            Assert.Equal(TimeSpan.FromSeconds(30), Grace(bad));
        Assert.Equal(TimeSpan.FromSeconds(10), Grace("10"));
        Assert.Equal(TimeSpan.FromSeconds(0.5), Grace("0.5"));
        Assert.Equal(TimeSpan.FromSeconds(120), Grace("120"));
        Assert.Equal(TimeSpan.FromSeconds(120), Grace("121"));
        Assert.Equal(TimeSpan.FromSeconds(120), Grace("1e300"));
        Assert.Equal(TimeSpan.FromSeconds(30), SessionStore.DefaultGrace);
    }

    [Fact]
    public void Signed_in_callers_are_partitioned_on_their_account_id_and_others_on_their_address()
    {
        var clients = new ClientAddress(false);
        var withId = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u@example.com"), new Claim(ClaimTypes.NameIdentifier, "42")], "test")),
        };
        Assert.Equal("u:42", RateLimiting.PartitionKey(withId, clients)); // #185: never the user name

        // #361: signed in without an id is the deny partition from any address, never the address.
        var noId = new DefaultHttpContext { User = SignedIn() };
        noId.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        Assert.Equal(RateLimiting.NoAccountIdKey, RateLimiting.PartitionKey(noId, clients));
        var noIdElsewhere = new DefaultHttpContext { User = SignedIn() };
        noIdElsewhere.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        Assert.Equal(RateLimiting.NoAccountIdKey, RateLimiting.PartitionKey(noIdElsewhere, clients));

        var anonymous = new DefaultHttpContext();
        anonymous.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        Assert.Equal("ip:203.0.113.7", RateLimiting.PartitionKey(anonymous, clients));
    }

    [Fact]
    public async Task The_limiter_still_refuses_a_principal_without_an_account_id_by_itself()
    {
        // SessionLimits answers such a token first (#399); the #361 refusal stays behind it as its own defence.
        var services = new ServiceCollection().AddLogging().AddProblemDetails()
            .AddDeskRateLimiting(LimitsOptions.From(new ConfigurationBuilder().Build()), new ClientAddress(false))
            .BuildServiceProvider();
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>>().Value;
        var http = new DefaultHttpContext { User = SignedIn(), RequestServices = services };
        http.Request.Path = "/api/me";
        http.Response.Body = new MemoryStream();

        using var lease = await options.GlobalLimiter!.AcquireAsync(http, 1, TestContext.Current.CancellationToken);
        Assert.False(lease.IsAcquired);
        await options.OnRejected!(new Microsoft.AspNetCore.RateLimiting.OnRejectedContext { HttpContext = http, Lease = lease }, TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.False(http.Response.Headers.ContainsKey("Retry-After"));
        http.Response.Body.Position = 0;
        Assert.Contains(Desk.Api.Auth.PrincipalExtensions.NoAccountIdTitle, await new StreamReader(http.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Only_a_signed_in_principal_with_a_non_empty_id_has_an_account_id()
    {
        var withId = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "42")], "test"));
        Assert.True(withId.TryGetAccountId(out var id));
        Assert.Equal("42", id);
        Assert.False(withId.HasNoAccountId());

        // Signed in, but the id is missing or empty: a broken session.
        foreach (var broken in new[] { SignedIn(), new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "")], "test")) })
        {
            Assert.False(broken.TryGetAccountId(out _));
            Assert.True(broken.HasNoAccountId());
        }

        // An unauthenticated identity's id is never trusted, and an anonymous caller isn't a broken session.
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "42")]));
        Assert.False(anonymous.TryGetAccountId(out var none));
        Assert.Null(none);
        Assert.False(anonymous.HasNoAccountId());
    }

    [Fact]
    public async Task The_no_account_id_partition_grants_nothing_and_is_never_idle()
    {
        var deny = RateLimiting.DenyAll.Instance;
        Assert.Null(deny.IdleDuration);
        Assert.Equal(0, deny.GetStatistics().CurrentAvailablePermits);
        using (var attempt = deny.AttemptAcquire())
        {
            Assert.False(attempt.IsAcquired);
            Assert.False(attempt.TryGetMetadata(MetadataName.RetryAfter, out _));
        }
        using (var lease = await deny.AcquireAsync(cancellationToken: Ct))
            Assert.False(lease.IsAcquired);
    }

    [Fact]
    public async Task AcquireOnce_refuses_the_synchronous_attempt_without_touching_the_limiter()
    {
        // #184: a token taken on the synchronous attempt is never given back, so only the asynchronous acquire counts.
        using var bucket = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetTokenBucketLimiter("k", _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 2, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromHours(1), QueueLimit = 0, AutoReplenishment = false,
        }));
        using var limiter = new RateLimiting.AcquireOnce(bucket);
        var http = new DefaultHttpContext();

        using (var attempt = limiter.AttemptAcquire(http))
        {
            Assert.False(attempt.IsAcquired);
            Assert.Empty(attempt.MetadataNames);
            Assert.False(attempt.TryGetMetadata(MetadataName.RetryAfter.Name, out var metadata));
            Assert.Null(metadata);
        }
        Assert.Equal(2, limiter.GetStatistics(http)!.CurrentAvailablePermits);

        using (var lease = await limiter.AcquireAsync(http, cancellationToken: TestContext.Current.CancellationToken))
            Assert.True(lease.IsAcquired);
        Assert.Equal(1, limiter.GetStatistics(http)!.CurrentAvailablePermits);
    }
}
