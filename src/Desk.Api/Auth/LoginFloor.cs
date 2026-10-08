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
/// Every 401 from <c>POST /api/auth/login</c> answers no sooner than <see cref="LoginFloorOptions.Floor"/> (plus jitter)
/// after the request reached it, so the time doesn't tell an unknown email from a real account, or one account state
/// from another (#230). Success is never delayed.
/// </summary>
/// <remarks>
/// It runs before the rate limiter, so the wait never holds one of the shared <c>/api</c> concurrency permits. The
/// response is buffered until the status is known; a login answer is a few hundred bytes. The clock is the injected
/// <see cref="TimeProvider"/>, and the wait ends early when the client goes away.
/// </remarks>
public sealed class LoginFloor(RequestDelegate next, LoginFloorOptions options, TimeProvider time)
{
    public static readonly PathString LoginPath = "/api/auth/login";

    public async Task InvokeAsync(HttpContext http)
    {
        if (!HttpMethods.IsPost(http.Request.Method) || !http.Request.Path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(http);
            return;
        }

        var started = time.GetTimestamp();
        var body = http.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        using var buffer = new MemoryStream();
        http.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(buffer));
        try
        {
            await next(http);
        }
        finally
        {
            http.Features.Set(body);
        }

        if (http.Response.StatusCode == StatusCodes.Status401Unauthorized)
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
