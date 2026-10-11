using System.Net;
using System.Net.Http.Json;
using Desk.Api.Hardening;
using Desk.Api.Limits;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Desk.Api.Tests;

/// <summary>
/// ADR-0024: a front end on GitHub Pages calls the API cross-origin. Only the configured origins get CORS headers,
/// never with credentials; preflights are answered before maintenance mode and the rate limiter.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CorsTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Pages = "https://pages.example.test";
    private const string Other = "https://evil.example.test";

    private WebApplicationFactory<Program> Host(params (string Key, string Value)[] settings) =>
        api.WithSettings([(CorsSetup.ConfigKey, $"{Pages}, http://localhost:4200"), .. settings]);

    private static HttpRequestMessage Preflight(string path, string origin, string method = "POST", string headers = "authorization,content-type")
    {
        var req = new HttpRequestMessage(HttpMethod.Options, path);
        req.Headers.Add("Origin", origin);
        req.Headers.Add("Access-Control-Request-Method", method);
        req.Headers.Add("Access-Control-Request-Headers", headers);
        return req;
    }

    private static string? Header(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;

    [Fact]
    public async Task An_allowed_origin_gets_cors_headers_without_credentials_on_a_signed_in_call()
    {
        await using var host = Host();
        var user = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, user.Email!);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        req.Headers.Add("Origin", Pages);
        var res = await client.SendAsync(req, Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(Pages, Header(res, "Access-Control-Allow-Origin"));
        Assert.Null(Header(res, "Access-Control-Allow-Credentials"));
        Assert.Contains("Origin", Header(res, "Vary"));
        Assert.Equal(Pages, Header(res, "Timing-Allow-Origin"));
        var exposed = Header(res, "Access-Control-Expose-Headers")!;
        Assert.All(CorsSetup.ExposedHeaders, h => Assert.Contains(h, exposed, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_disallowed_origin_gets_no_cors_headers()
    {
        await using var host = Host();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("Origin", Other);
        var res = await PostgresApiFactory.NewClient(host).SendAsync(req, Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // the browser, not the server, blocks the read
        Assert.Null(Header(res, "Access-Control-Allow-Origin"));
        Assert.Null(Header(res, "Timing-Allow-Origin"));
    }

    [Fact]
    public async Task No_origin_is_allowed_by_default()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("Origin", Pages);
        var res = await api.NewClient().SendAsync(req, Ct);
        Assert.Null(Header(res, "Access-Control-Allow-Origin"));

        var preflight = await api.NewClient().SendAsync(Preflight("/api/positions/query", Pages), Ct);
        Assert.Null(Header(preflight, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_preflight_from_an_allowed_origin_allows_the_token_header_and_the_methods()
    {
        await using var host = Host();
        var res = await PostgresApiFactory.NewClient(host).SendAsync(Preflight("/api/presets/positions", Pages, "PUT", "authorization,content-type,if-none-match"), Ct);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(Pages, Header(res, "Access-Control-Allow-Origin"));
        Assert.Null(Header(res, "Access-Control-Allow-Credentials"));
        Assert.Contains("PUT", Header(res, "Access-Control-Allow-Methods"));
        var allowed = Header(res, "Access-Control-Allow-Headers")!;
        Assert.Contains("authorization", allowed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if-none-match", allowed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("600", Header(res, "Access-Control-Max-Age"));
    }

    [Fact]
    public async Task A_preflight_from_another_origin_is_answered_without_cors_headers()
    {
        await using var host = Host();
        var res = await PostgresApiFactory.NewClient(host).SendAsync(Preflight("/api/positions/query", Other), Ct);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Null(Header(res, "Access-Control-Allow-Origin"));
        Assert.Null(Header(res, "Access-Control-Allow-Headers"));
    }

    [Theory]
    [InlineData("PATCH", "authorization", "PATCH", null)]      // not a method the API uses
    [InlineData("POST", "authorization,x-custom", null, "x-custom")] // not a header the SPA sends
    public async Task A_preflight_outside_the_policy_never_lists_what_it_asked_for(string method, string headers, string? deniedMethod, string? deniedHeader)
    {
        // ASP.NET Core answers with the policy's own lists; the browser refuses the call because its method or
        // header isn't on them.
        await using var host = Host();
        var res = await PostgresApiFactory.NewClient(host).SendAsync(Preflight("/api/positions/query", Pages, method, headers), Ct);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        if (deniedMethod is not null)
            Assert.DoesNotContain(deniedMethod, Header(res, "Access-Control-Allow-Methods") ?? "", StringComparison.OrdinalIgnoreCase);
        if (deniedHeader is not null)
            Assert.DoesNotContain(deniedHeader, Header(res, "Access-Control-Allow-Headers") ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preflights_never_draw_on_the_rate_limit_and_never_open_a_connection()
    {
        // One anonymous token a minute for this client's IP: preflights must not spend it.
        await using var host = Host(("RATE_LIMIT_PER_USER_PER_MIN", "1"), ("RATE_LIMIT_PER_USER_BURST", "1"));
        var client = PostgresApiFactory.NewClient(host);
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Preflight("/api/positions/query", Pages), Ct)).StatusCode);
        Assert.Equal(0, api.ConnectionsOpened(host));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        using var limited = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        limited.Headers.Add("Origin", Pages);
        var res = await client.SendAsync(limited, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        // The SPA on another origin can only read the 429 (and its Retry-After) with these.
        Assert.Equal(Pages, Header(res, "Access-Control-Allow-Origin"));
        Assert.Contains("Retry-After", Header(res, "Access-Control-Expose-Headers"));
    }

    [Fact]
    public async Task Maintenance_mode_answers_preflights_and_its_503_is_readable_cross_origin()
    {
        await using var host = Host((MaintenanceMode.ConfigKey, "true"));
        var client = PostgresApiFactory.NewClient(host);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Preflight("/api/auth/login", Pages, "POST", "content-type"), Ct)).StatusCode);

        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email = "a@example.com", password = "x" }) };
        login.Headers.Add("Origin", Pages);
        var res = await client.SendAsync(login, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal(Pages, Header(res, "Access-Control-Allow-Origin"));
        Assert.Equal(0, api.ConnectionsOpened(host));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("https://llevintza.github.io", new[] { "https://llevintza.github.io" })]
    [InlineData("https://LLEVINTZA.github.io/ , http://localhost:4200", new[] { "https://llevintza.github.io", "http://localhost:4200" })]
    [InlineData("https://a.test https://a.test\thttps://b.test:8443", new[] { "https://a.test", "https://b.test:8443" })]
    // Not an origin: a path, a query, credentials, another scheme, a wildcard or a bare host are dropped, never widened.
    [InlineData("https://a.test/app,https://b.test?x=1,https://u:p@c.test,ftp://d.test,*,e.test,https://f.test/#top", new string[0])]
    public void Only_http_and_https_origins_are_kept(string? value, string[] expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new(CorsSetup.ConfigKey, value)]).Build();
        Assert.Equal(expected, CorsSetup.Origins(config));
    }
}
