using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;

namespace Desk.Api.Auth;

/// <summary>How long a failed login takes at least, and the random extra on top (#230).</summary>
/// <param name="Floor">Above the p99 of the slowest failed path (a wrong password on an active account).</param>
/// <param name="MaxJitter">Up to this much is added to each padded 401, so the floor itself isn't a sharp edge.</param>
public sealed record LoginFloorOptions(TimeSpan Floor, TimeSpan MaxJitter)
{
    /// <summary>The only production value; tests replace it through <c>ConfigureTestServices</c>. No setting turns it off.</summary>
    public static readonly LoginFloorOptions Default = new(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(50));
}

/// <summary>
/// Every response with the padded status of an endpoint marked with <see cref="LoginFloorMetadata"/> answers no sooner
/// than <see cref="LoginFloorOptions.Floor"/> (plus jitter) after the request reached it: the 401s of
/// <c>POST /api/auth/login</c>, so the time doesn't tell an unknown email from a real account, or one account state
/// from another (#230; a successful login is never delayed), and the 202s of <c>POST /api/access-requests</c>, so it
/// doesn't tell a new request from a duplicate, an existing account or a refused bot either (ADR-0029, #538).
/// </summary>
/// <remarks>
/// It runs before the rate limiter, so the wait never holds one of the shared <c>/api</c> concurrency permits. The
/// response is buffered until the status is known; a login answer is a few hundred bytes. The clock is the injected
/// <see cref="TimeProvider"/>, and the wait ends early when the client goes away. It runs after <c>UseRouting</c>, so
/// the endpoint is known.
/// </remarks>
public sealed class LoginFloor(RequestDelegate next, LoginFloorOptions options, TimeProvider time)
{
    public async Task InvokeAsync(HttpContext http)
    {
        // The routed endpoint, not the path string: routing also matches a trailing slash in any casing (R279-01).
        if (http.GetEndpoint()?.Metadata.GetMetadata<LoginFloorMetadata>() is not { } floor)
        {
            await next(http);
            return;
        }

        var started = time.GetTimestamp();
        var body = http.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        using var buffer = new MemoryStream();
        var buffering = new StreamResponseBodyFeature(buffer);
        http.Features.Set<IHttpResponseBodyFeature>(buffering);
        try
        {
            await next(http);
            // Flushes anything still in the BodyWriter pipe into the buffer (R279-02).
            await buffering.CompleteAsync();
        }
        finally
        {
            http.Features.Set(body);
        }

        if (http.Response.StatusCode == floor.Status)
        {
            var remaining = options.Floor + Jitter() - time.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, time, http.RequestAborted);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(http.Response.Body, http.RequestAborted);
    }

    private TimeSpan Jitter() =>
        TimeSpan.FromMicroseconds(RandomNumberGenerator.GetInt32((int)options.MaxJitter.TotalMicroseconds + 1));
}

/// <summary>Marks an endpoint whose responses with <see cref="Status"/> <see cref="LoginFloor"/> pads.</summary>
public sealed class LoginFloorMetadata
{
    /// <summary>The login's 401s.</summary>
    public static readonly LoginFloorMetadata Instance = new(StatusCodes.Status401Unauthorized);

    /// <summary>The access request's 202s (ADR-0029 §1): every outcome a caller may not tell apart answers 202.</summary>
    public static readonly LoginFloorMetadata Accepted = new(StatusCodes.Status202Accepted);

    private LoginFloorMetadata(int status) => Status = status;

    /// <summary>The one status that is padded; any other answers at once.</summary>
    public int Status { get; }
}
