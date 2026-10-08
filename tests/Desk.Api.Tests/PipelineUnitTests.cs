using System.Security.Claims;
using Desk.Api.Audit;
using Desk.Api.Auth;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Antiforgery_lets_safe_methods_through_without_a_token(string method)
    {
        var filter = new AntiforgeryFilter(new RejectingAntiforgery());
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>("next"));
        Assert.Equal("next", result);
    }

    [Fact]
    public async Task Antiforgery_rejects_an_unsafe_method_without_a_valid_token()
    {
        var filter = new AntiforgeryFilter(new RejectingAntiforgery());
        var http = new DefaultHttpContext();
        http.Request.Method = "DELETE";
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>("next"));
        Assert.NotEqual("next", result);
    }

    public static TheoryData<string?, bool> BadTickets => new()
    {
        { null, true },              // no auth_time recorded
        { "not-a-time", true },      // tampered or garbage auth_time
        { "2026-10-07T12:00:00.0000000+00:00", false }, // fine time, but no expiry claim
    };

    [Theory]
    [MemberData(nameof(BadTickets))]
    public async Task A_session_without_a_valid_auth_time_or_expiry_is_rejected(string? authTime, bool withExpiry)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddAuthentication().AddCookie(IdentityConstants.ApplicationScheme);
        await using var sp = services.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = sp };

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "u@example.com")], "test");
        if (withExpiry)
            identity.AddClaim(new Claim(DeskClaimsFactory.ExpiresAtClaim, DateTimeOffset.UtcNow.AddDays(1).ToString("O")));
        var props = new AuthenticationProperties();
        if (authTime is not null) props.Items[AuthSetup.AuthTimeKey] = authTime;
        var scheme = new AuthenticationScheme(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler));
        var ctx = new CookieValidatePrincipalContext(http, scheme, new CookieAuthenticationOptions(),
            new AuthenticationTicket(new ClaimsPrincipal(identity), props, scheme.Name));

        await AuthSetup.ValidatePrincipalAsync(ctx);
        Assert.Null(ctx.Principal);
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

    private sealed class RejectingAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();
        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(false);
        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();
        public Task ValidateRequestAsync(HttpContext httpContext) => throw new NotSupportedException();
    }
}
