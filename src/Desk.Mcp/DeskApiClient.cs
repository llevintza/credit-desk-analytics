using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Desk.Mcp;

internal sealed record TokenPair(string? TokenType, string AccessToken, long ExpiresIn, string RefreshToken);

/// <summary>
/// A typed client of <c>/api</c> (ADR-0031 §2): the access token lives in memory only and is refreshed on demand (expired
/// at call time, or once on a 401), never on a timer. A refused refresh means "sign in again". Every failure surfaces as an
/// <see cref="McpException"/> whose message carries the status and title, never a token or an email.
/// </summary>
internal sealed class DeskApiClient(HttpClient http, TokenStore store, RequestGate gate, TimeProvider time, ILogger<DeskApiClient> log)
{
    /// <summary>The longest total wait on 429 + Retry-After before a call reports "rate limited".</summary>
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(60);

    /// <summary>How long before its stated expiry an access token counts as expired, for clock skew and latency.</summary>
    public static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string? _access;
    private DateTimeOffset _expires;

    public ApiOrigin Origin => store.Origin;

    public string SignInAgainMessage => $"The sign-in to {Origin} has ended. Sign in again with `desk-mcp login --api {Origin}`.";

    /// <summary>GETs <paramref name="path"/> as the signed-in account and returns the JSON body.</summary>
    public async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var slot = await gate.EnterAsync(ct);
        var refreshed = false;
        if (_access is null || time.GetUtcNow() >= _expires)
        {
            await RefreshAsync(ct);
            refreshed = true;
        }
        while (true)
        {
            using var res = await SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
                return req;
            }, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                _access = null;
                if (refreshed) throw new McpException(SignInAgainMessage);
                log.LogInformation("The API refused the access token; refreshing once");
                await RefreshAsync(ct);
                refreshed = true;
                continue;
            }
            if (!res.IsSuccessStatusCode) throw await ProblemAsync(res, ct);
            return await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        }
    }

    /// <summary>Swaps the saved refresh token under the file lock, re-reading it first so another process's swap counts.</summary>
    private async Task RefreshAsync(CancellationToken ct)
    {
        using var held = await LockAsync(ct);
        var saved = store.Read() ?? throw new McpException($"Not signed in to {Origin}. Run `desk-mcp login --api {Origin}` first.");
        var tokens = await ExchangeAsync(saved.RefreshToken, ct);
        if (tokens is null)
        {
            store.Delete();
            log.LogWarning("The API refused the refresh token; the sign-in has ended");
            throw new McpException(SignInAgainMessage);
        }
        store.Write(tokens.RefreshToken);
        Accept(tokens);
        log.LogInformation("Refreshed the access token");
    }

    /// <summary>POST /api/auth/refresh. Null when the API refuses the token (401); any other failure throws.</summary>
    internal async Task<TokenPair?> ExchangeAsync(string refreshToken, CancellationToken ct)
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken }, options: Json),
        }, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (!res.IsSuccessStatusCode) throw await ProblemAsync(res, ct);
        return await ReadTokensAsync(res, ct);
    }

    /// <summary>POST /api/auth/login. Null when the API refuses the credentials (401); any other failure throws.</summary>
    public async Task<TokenPair?> LoginAsync(string email, string password, CancellationToken ct)
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password }, options: Json),
        }, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (!res.IsSuccessStatusCode) throw await ProblemAsync(res, ct);
        return await ReadTokensAsync(res, ct);
    }

    /// <summary>Ends the saved sign-in: swaps the refresh token for an access token, POSTs /api/auth/logout, deletes the file.</summary>
    /// <returns>False when there was no saved sign-in.</returns>
    public async Task<bool> LogoutAsync(CancellationToken ct)
    {
        using var held = await LockAsync(ct);
        if (store.Read() is not { } saved) return false;
        try
        {
            if (await ExchangeAsync(saved.RefreshToken, ct) is { } tokens)
            {
                using var res = await SendAsync(() =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
                    return req;
                }, ct);
                if (!res.IsSuccessStatusCode) throw await ProblemAsync(res, ct);
            }
        }
        finally
        {
            store.Delete();
            _access = null;
        }
        return true;
    }

    private async Task<IDisposable> LockAsync(CancellationToken ct)
    {
        try
        {
            return await store.LockAsync(LockTimeout, ct);
        }
        catch (IOException)
        {
            throw new McpException($"Another desk-mcp process holds the sign-in lock for {Origin}; try again.");
        }
    }

    private void Accept(TokenPair tokens)
    {
        _access = tokens.AccessToken;
        _expires = time.GetUtcNow() + TimeSpan.FromSeconds(tokens.ExpiresIn) - ExpirySkew;
    }

    private static async Task<TokenPair> ReadTokensAsync(HttpResponseMessage res, CancellationToken ct) =>
        await res.Content.ReadFromJsonAsync<TokenPair>(Json, ct) is { AccessToken.Length: > 0, RefreshToken.Length: > 0 } tokens
            ? tokens
            : throw new McpException("The desk API returned no tokens.");

    /// <summary>
    /// Sends one request through the bucket. A 429 waits for its Retry-After and tries again, up to
    /// <see cref="MaxRetryWait"/> in all; without a Retry-After, or past that, it reports "rate limited".
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            await gate.TakeAsync(ct);
            HttpResponseMessage res;
            using (var req = request())
            {
                try
                {
                    res = await http.SendAsync(req, ct);
                }
                catch (HttpRequestException e)
                {
                    throw new McpException($"Could not reach the desk API at {Origin} ({Redact.Text(e.Message)}).");
                }
            }
            if (res.StatusCode != HttpStatusCode.TooManyRequests) return res;
            var delay = RetryAfter(res);
            res.Dispose();
            if (delay is not { } d || waited + d > MaxRetryWait)
                throw new McpException("Rate limited by the desk API. Try again in a minute.");
            log.LogInformation("Rate limited; waiting {Seconds} s as Retry-After asks", (int)Math.Ceiling(d.TotalSeconds));
            waited += d;
            await Task.Delay(d, time, ct);
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage res) => res.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta < TimeSpan.Zero ? TimeSpan.Zero : delta,
        { Date: { } date } => date - time.GetUtcNow() is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero,
        _ => null,
    };

    /// <summary>A ProblemDetails (or any failed) response as an MCP tool error: status, title and a redacted detail.</summary>
    internal static async Task<McpException> ProblemAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var status = (int)res.StatusCode;
        string? title = null, detail = null;
        try
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (body.Length > 0 && JsonDocument.Parse(body).RootElement is { ValueKind: JsonValueKind.Object } problem)
            {
                title = problem.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                detail = problem.TryGetProperty("detail", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            }
        }
        catch (JsonException)
        {
            // Not a ProblemDetails body: the status and reason phrase are all there is.
        }
        title ??= res.ReasonPhrase ?? res.StatusCode.ToString();
        var text = status == 503 && title == "Maintenance"
            ? $"The desk is in maintenance mode (503): {detail}"
            : $"The desk API answered {status} {title}" + (string.IsNullOrEmpty(detail) ? "." : $": {detail}");
        return new McpException(Redact.Text(text));
    }
}
