using System.Net;
using System.Net.Http.Json;
using Desk.Api.Auth;
using Desk.Api.Limits;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;

namespace Desk.Api.Tests;

/// <summary>
/// #284 (R279-01): routing matches a path in any casing and with a trailing slash, so every middleware that decides
/// by the path string must treat those spellings as the endpoint they route to. Login and one data endpoint, each in
/// every spelling routing accepts, get the same maintenance, limiter, authentication and antiforgery behaviour as the
/// canonical path. The login floor's own spelling theory is in <see cref="LoginTimingTests"/>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RouteEquivalentPathTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Login = "/api/auth/login";
    private const string Query = "/api/positions/query";

    public static TheoryData<string> LoginSpellings =>
    [
        Login,
        "/api/auth/login/",
        "/API/AUTH/LOGIN",
        "/Api/Auth/Login/",
        // HttpClient, like Kestrel, removes dot segments before the app sees the path.
        "/api/./auth/login",
    ];

    public static TheoryData<string> QuerySpellings =>
    [
        Query,
        "/api/positions/query/",
        "/API/POSITIONS/QUERY",
        "/Api/Positions/Query/",
        "/api/./positions/query",
    ];

    private static HttpRequestMessage Post(string path, object body, string? xsrf = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (xsrf is not null) req.Headers.Add(AuthSetup.AntiforgeryHeaderName, xsrf);
        return req;
    }

    private static HttpRequestMessage WrongLogin(string path) => Post(path, new LoginRequest("nobody@example.com", "not-the-password"));

    [Theory]
    [MemberData(nameof(LoginSpellings))]
    [MemberData(nameof(QuerySpellings))]
    public async Task Maintenance_mode_answers_every_spelling_with_503_and_no_connection(string path)
    {
        await using var host = api.WithSettings((MaintenanceMode.ConfigKey, "true"));

        var res = await PostgresApiFactory.NewClient(host).SendAsync(Post(path, new { }), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("300", res.Headers.GetValues("Retry-After").Single());
        Assert.Equal(MaintenanceMode.Detail, (await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail);
        Assert.Equal(0, api.ConnectionsOpened(host));
    }

    [Theory]
    [MemberData(nameof(LoginSpellings))]
    public async Task Every_spelling_of_login_counts_against_the_same_per_ip_window(string path)
    {
        await using var host = api.WithSettings(("RATE_LIMIT_LOGIN_PER_IP_PER_MIN", "1"));
        var client = PostgresApiFactory.NewClient(host);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WrongLogin(Login), Ct)).StatusCode);
        var limited = await client.SendAsync(WrongLogin(path), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("Too many requests", (await limited.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
    }

    [Theory]
    [MemberData(nameof(QuerySpellings))]
    public async Task Every_spelling_of_a_data_endpoint_draws_on_the_same_token_bucket(string path)
    {
        // One token a minute and a burst of one: the canonical query spends it, and nothing refills in between. The
        // login is anonymous when the limiter sees it, so it draws on the caller's IP bucket, not this one.
        await using var host = api.WithSettings(("RATE_LIMIT_PER_USER_PER_MIN", "1"), ("RATE_LIMIT_PER_USER_BURST", "1"));
        var user = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);
        var xsrf = await PostgresApiFactory.LoginAsync(client, user.Email!);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(Query, new { }, xsrf), Ct)).StatusCode);
        var limited = await client.SendAsync(Post(path, new { }, xsrf), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("Too many requests", (await limited.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
    }

    [Theory]
    [MemberData(nameof(QuerySpellings))]
    public async Task Every_spelling_of_a_data_endpoint_needs_a_session_and_an_antiforgery_token(string path)
    {
        var (client, xsrf, _) = await api.SignedInAsync();

        // The session cookie is only read on session paths (Program.cs): a spelling outside them would be anonymous.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(path, new { }, xsrf), Ct)).StatusCode);

        var noToken = await client.SendAsync(Post(path, new { }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(AntiforgeryFilter.ProblemTitle, (await noToken.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().SendAsync(Post(path, new { }, xsrf), Ct)).StatusCode);
    }

    /// <summary>
    /// The invariant behind the theories: a request reaches an <c>/api</c> endpoint exactly when the path-string checks
    /// (<see cref="MaintenanceMode.IsSessionPath"/>, which gates authentication, and the same <c>/api</c> prefix in
    /// the limiter and audit) see it as <c>/api</c>. Sent through the test server unnormalized, so dot segments and
    /// empty segments reach routing as written.
    /// </summary>
    [Theory]
    [InlineData("POST", Login, true)]
    [InlineData("POST", "/API/Auth/Login/", true)]
    [InlineData("POST", Query, true)]
    [InlineData("POST", "/api/positions/QUERY/", true)]
    [InlineData("GET", "/api/me/", true)]
    [InlineData("GET", "/API/ME", true)]
    // Routing doesn't collapse empty or dot segments: these never reach the endpoint they resemble.
    [InlineData("POST", "/api//auth/login", true)]
    [InlineData("POST", "/api/./positions/query", true)]
    [InlineData("POST", "//api/auth/login", false)]
    [InlineData("POST", "/./api/positions/query", false)]
    [InlineData("GET", "//api/me", false)]
    [InlineData("GET", "/./api/me", false)]
    public async Task A_request_reaches_an_api_endpoint_only_on_a_path_the_api_middleware_sees(string method, string path, bool reachesApi)
    {
        var http = await api.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Scheme = "https";
            c.Request.Path = path;
        }, Ct);

        var routedToApi = http.GetEndpoint() is RouteEndpoint route && route.RoutePattern.RawText!.StartsWith("/api", StringComparison.Ordinal);
        Assert.Equal(reachesApi, routedToApi);
        Assert.Equal(routedToApi, MaintenanceMode.IsSessionPath(http));
    }
}
