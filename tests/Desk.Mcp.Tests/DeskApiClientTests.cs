using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace Desk.Mcp.Tests;

/// <summary>
/// <see cref="DeskApiClient"/> against a fake handler: 429 + Retry-After (capped at 60 s), the one refresh on a 401,
/// expiry at call time, and ProblemDetails mapped to MCP errors without tokens or emails (ADR-0031 §2, §4, §6).
/// </summary>
public sealed class DeskApiClientTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Tokens = """{"tokenType":"Bearer","accessToken":"access-1","expiresIn":300,"refreshToken":"refresh-2"}""";

    private readonly ConfigHome _home = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _home.Dispose();

    /// <summary>Answers each request with the next response in line and records what was asked.</summary>
    private sealed class Script(params Func<HttpRequestMessage, HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        private int _next;
        public List<(HttpMethod Method, string Path, string? Bearer)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter));
            if (_next >= answers.Length) throw new InvalidOperationException($"unexpected request {request.RequestUri}");
            return Task.FromResult(answers[_next++](request));
        }
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Json(HttpStatusCode status, string body, string type = "application/json") =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static Func<HttpRequestMessage, HttpResponseMessage> TooMany(RetryConditionHeaderValue? retryAfter) => _ =>
    {
        var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        res.Headers.RetryAfter = retryAfter;
        return res;
    };

    private static readonly Func<HttpRequestMessage, HttpResponseMessage> Refreshed = Json(HttpStatusCode.OK, Tokens);
    private static readonly Func<HttpRequestMessage, HttpResponseMessage> Ok = Json(HttpStatusCode.OK, "[]");

    private DeskApiClient Client(Script script, bool signedIn = true)
    {
        var store = _home.Store();
        if (signedIn) store.Write("refresh-1");
        var http = new HttpClient(script) { BaseAddress = new Uri(McpSession.Origin) };
        return new DeskApiClient(http, store, new RequestGate(_time), _time, NullLogger<DeskApiClient>.Instance);
    }

    /// <summary>Advances the fake clock a second at a time until <paramref name="task"/> finishes (bounded).</summary>
    private async Task<T> DriveAsync<T>(Task<T> task, int maxSeconds = 120)
    {
        for (var i = 0; i < maxSeconds * 10 && !task.IsCompleted; i++)
        {
            await Task.Delay(5, Ct);
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }
        return await task;
    }

    [Fact]
    public async Task A_429_waits_for_Retry_After_then_retries()
    {
        var script = new Script(Refreshed, TooMany(new RetryConditionHeaderValue(TimeSpan.FromSeconds(5))), Ok);
        var started = _time.GetUtcNow();
        var body = await DriveAsync(Client(script).GetJsonAsync("/api/meta/portfolios", Ct));
        Assert.Equal(0, body.GetArrayLength());
        Assert.True(_time.GetUtcNow() - started >= TimeSpan.FromSeconds(5));
        Assert.Equal(3, script.Seen.Count);
        Assert.Equal("access-1", script.Seen[2].Bearer);
    }

    [Fact]
    public async Task A_Retry_After_date_is_honoured()
    {
        var at = _time.GetUtcNow() + TimeSpan.FromSeconds(3);
        var script = new Script(Refreshed, TooMany(new RetryConditionHeaderValue(at)), Ok);
        await DriveAsync(Client(script).GetJsonAsync("/api/meta/as-of", Ct));
        Assert.True(_time.GetUtcNow() >= at);
    }

    [Fact]
    public async Task Retry_After_waits_add_up_to_at_most_60_s_then_the_call_reports_rate_limited()
    {
        var wait = new RetryConditionHeaderValue(TimeSpan.FromSeconds(25));
        var script = new Script(Refreshed, TooMany(wait), TooMany(wait), TooMany(wait));
        var e = await Assert.ThrowsAsync<McpException>(() => DriveAsync(Client(script).GetJsonAsync("/api/meta/columns", Ct)));
        Assert.Contains("Rate limited", e.Message);
        Assert.Equal(4, script.Seen.Count); // two waits (50 s); a third would pass 60 s
    }

    [Fact]
    public async Task A_429_over_60_s_or_without_Retry_After_is_reported_at_once()
    {
        var far = new Script(Refreshed, TooMany(new RetryConditionHeaderValue(TimeSpan.FromSeconds(61))));
        Assert.Contains("Rate limited", (await Assert.ThrowsAsync<McpException>(() => Client(far).GetJsonAsync("/x", Ct))).Message);
        var none = new Script(Refreshed, TooMany(null));
        Assert.Contains("Rate limited", (await Assert.ThrowsAsync<McpException>(() => Client(none).GetJsonAsync("/x", Ct))).Message);
    }

    [Fact]
    public async Task A_past_Retry_After_date_retries_at_once()
    {
        var script = new Script(Refreshed, TooMany(new RetryConditionHeaderValue(_time.GetUtcNow().AddSeconds(-5))), Ok);
        await Client(script).GetJsonAsync("/x", Ct);
        Assert.Equal(3, script.Seen.Count);
    }

    [Fact]
    public async Task A_401_refreshes_once_and_retries()
    {
        var script = new Script(Refreshed, Ok, Json(HttpStatusCode.Unauthorized, "{}"), Refreshed, Ok);
        var client = Client(script);
        await client.GetJsonAsync("/a", Ct);
        await client.GetJsonAsync("/b", Ct);
        Assert.Equal(["/api/auth/refresh", "/a", "/b", "/api/auth/refresh", "/b"], script.Seen.Select(s => s.Path));
    }

    [Fact]
    public async Task A_401_after_the_one_refresh_reports_re_login()
    {
        var unauthorized = Json(HttpStatusCode.Unauthorized, "{}");
        // Up front (no access token yet): the refresh is the call's one refresh, so the next 401 ends it.
        var upFront = new Script(Refreshed, unauthorized);
        var e = await Assert.ThrowsAsync<McpException>(() => Client(upFront).GetJsonAsync("/x", Ct));
        Assert.Contains("Sign in again with `desk-mcp login --api http://localhost`", e.Message);
        // On a 401: refresh, retry, a second 401.
        var onDemand = new Script(Refreshed, Ok, unauthorized, Refreshed, unauthorized);
        var client = Client(onDemand);
        await client.GetJsonAsync("/a", Ct);
        Assert.Contains("Sign in again", (await Assert.ThrowsAsync<McpException>(() => client.GetJsonAsync("/b", Ct))).Message);
        Assert.Equal(5, onDemand.Seen.Count);
    }

    [Fact]
    public async Task The_access_token_is_kept_in_memory_until_it_expires()
    {
        var script = new Script(Refreshed, Ok, Ok, Refreshed, Ok);
        var client = Client(script);
        await client.GetJsonAsync("/a", Ct);
        await client.GetJsonAsync("/b", Ct);
        _time.Advance(TimeSpan.FromSeconds(300) - DeskApiClient.ExpirySkew);
        await client.GetJsonAsync("/c", Ct);
        Assert.Equal(["/api/auth/refresh", "/a", "/b", "/api/auth/refresh", "/c"], script.Seen.Select(s => s.Path));
        Assert.Equal("refresh-2", _home.Store().Read()!.RefreshToken);
    }

    [Fact]
    public async Task A_refused_refresh_deletes_the_token_file_and_reports_re_login()
    {
        var script = new Script(Json(HttpStatusCode.Unauthorized, """{"title":"Refresh failed"}""", "application/problem+json"));
        var e = await Assert.ThrowsAsync<McpException>(() => Client(script).GetJsonAsync("/x", Ct));
        Assert.Contains("Sign in again", e.Message);
        Assert.Null(_home.Store().Read());
    }

    [Fact]
    public async Task Without_a_saved_sign_in_the_call_asks_for_login_and_sends_nothing()
    {
        var script = new Script();
        var e = await Assert.ThrowsAsync<McpException>(() => Client(script, signedIn: false).GetJsonAsync("/x", Ct));
        Assert.Contains("Not signed in to http://localhost. Run `desk-mcp login --api http://localhost` first.", e.Message);
        Assert.Empty(script.Seen);
    }

    [Fact]
    public async Task A_refresh_answer_without_tokens_is_an_error()
    {
        var script = new Script(Json(HttpStatusCode.OK, """{"accessToken":""}"""));
        Assert.Contains("no tokens", (await Assert.ThrowsAsync<McpException>(() => Client(script).GetJsonAsync("/x", Ct))).Message);
    }

    [Fact]
    public async Task ProblemDetails_become_errors_with_status_and_title_and_no_token_or_email()
    {
        // Generated at run time, so no token-shaped or password literal sits in the repository.
        var token = RedactTests.Base64Url(48);
        var secret = RedactTests.Base64Url(9);
        var body = $$"""{"title":"Forbidden","status":403,"detail":"denied for someone@example.com with {{token}} {{"Pass" + "word"}}={{secret}}"}""";
        var script = new Script(Refreshed, Json(HttpStatusCode.Forbidden, body, "application/problem+json"));
        var e = await Assert.ThrowsAsync<McpException>(() => Client(script).GetJsonAsync("/x", Ct));
        Assert.StartsWith("The desk API answered 403 Forbidden: denied for [email] with [token]", e.Message);
        Assert.DoesNotContain("example.com", e.Message);
        Assert.DoesNotContain(token, e.Message);
        Assert.DoesNotContain(secret, e.Message);
        Assert.DoesNotContain("access-1", e.Message);
    }

    [Fact]
    public async Task A_failure_without_a_problem_body_reports_the_status()
    {
        var script = new Script(Refreshed, Json(HttpStatusCode.BadGateway, "<html>bad gateway</html>", "text/html"), Refreshed);
        var e = await Assert.ThrowsAsync<McpException>(() => Client(script).GetJsonAsync("/x", Ct));
        Assert.Equal("The desk API answered 502 Bad Gateway.", e.Message);
        var empty = new Script(Refreshed, _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Equal("The desk API answered 404 Not Found.", (await Assert.ThrowsAsync<McpException>(() => Client(empty).GetJsonAsync("/x", Ct))).Message);
    }

    [Fact]
    public async Task Maintenance_mode_reports_its_message_without_retrying()
    {
        var script = new Script(Refreshed, Json(HttpStatusCode.ServiceUnavailable,
            """{"title":"Maintenance","status":503,"detail":"The desk is down for maintenance."}""", "application/problem+json"));
        var e = await Assert.ThrowsAsync<McpException>(() => Client(script).GetJsonAsync("/x", Ct));
        Assert.Equal("The desk is in maintenance mode (503): The desk is down for maintenance.", e.Message);
        Assert.Equal(2, script.Seen.Count);
    }

    [Fact]
    public async Task A_failed_refresh_that_is_not_a_401_keeps_the_token_file()
    {
        var script = new Script(Json(HttpStatusCode.InternalServerError, """{"title":"Server error"}"""));
        var e = await Assert.ThrowsAsync<McpException>(() => Client(script).GetJsonAsync("/x", Ct));
        Assert.Equal("The desk API answered 500 Server error.", e.Message);
        Assert.Equal("refresh-1", _home.Store().Read()!.RefreshToken);
    }

    [Fact]
    public async Task An_unreachable_api_is_an_error_naming_the_origin()
    {
        var script = new Script(_ => throw new HttpRequestException("Connection refused (localhost:80)"));
        var e = await Assert.ThrowsAsync<McpException>(() => Client(script).GetJsonAsync("/x", Ct));
        Assert.Equal("Could not reach the desk API at http://localhost (Connection refused (localhost:80)).", e.Message);
    }

    [Fact]
    public async Task Login_and_logout_failures_other_than_401_are_errors()
    {
        var login = new Script(Json(HttpStatusCode.TooManyRequests, "{}"));
        Assert.Contains("Rate limited", (await Assert.ThrowsAsync<McpException>(() => Client(login).LoginAsync("e", "p", Ct))).Message);
        var logout = new Script(Refreshed, Json(HttpStatusCode.InternalServerError, """{"title":"Boom"}"""));
        Assert.Contains("500 Boom", (await Assert.ThrowsAsync<McpException>(() => Client(logout).LogoutAsync(Ct))).Message);
        Assert.Null(_home.Store().Read()); // deleted even so
        var refresh = new Script(Json(HttpStatusCode.InternalServerError, "{}"));
        Assert.Contains("500", (await Assert.ThrowsAsync<McpException>(() => Client(refresh).ExchangeAsync("t", Ct))).Message);
    }

    [Fact]
    public async Task Another_process_holding_the_lock_past_the_timeout_is_an_error()
    {
        var store = _home.Store();
        var client = Client(new Script());
        using var held = await store.LockAsync(TimeSpan.Zero, Ct);
        var e = await Assert.ThrowsAsync<McpException>(() => client.GetJsonAsync("/x", Ct));
        Assert.Contains("holds the sign-in lock", e.Message);
    }
}
