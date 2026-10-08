using System.Globalization;
using System.Threading.RateLimiting;
using Desk.Api.Auth;
using Microsoft.AspNetCore.RateLimiting;

namespace Desk.Api.Limits;

/// <summary>
/// Free-tier protection (README §7.2): a per-user token bucket and one shared concurrency limiter on all of
/// <c>/api</c> (every endpoint there can reach the database), plus a per-IP window on login.
/// </summary>
public static class RateLimiting
{
    public const string LoginPolicy = "login";

    public static IServiceCollection AddDeskRateLimiting(this IServiceCollection services, LimitsOptions limits)
    {
        services.AddSingleton(limits);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = OnRejectedAsync;

            // Every /api request passes both: the caller's token bucket, then one shared concurrency limiter sized to
            // the DB connection budget. Applying it to the whole group means no endpoint can forget to opt in.
            var perUser = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                !IsApi(http)
                    ? RateLimitPartition.GetNoLimiter("static")
                    : RateLimitPartition.GetTokenBucketLimiter(PartitionKey(http), _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = limits.PerUserBurst,
                        TokensPerPeriod = 1,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1) / limits.PerUserPerMinute,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            var database = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                !IsApi(http)
                    ? RateLimitPartition.GetNoLimiter("static")
                    : RateLimitPartition.GetConcurrencyLimiter("db", _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = limits.GlobalConcurrency,
                        QueueLimit = limits.GlobalQueue,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    }));
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(perUser, database);

            o.AddPolicy(LoginPolicy, http => RateLimitPartition.GetFixedWindowLimiter(ClientIp(http), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.LoginPerIpPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
        });
        return services;
    }

    private static bool IsApi(HttpContext http) => http.Request.Path.StartsWithSegments("/api");

    /// <summary>Signed-in users get their own bucket; anonymous callers share one per IP.</summary>
    internal static string PartitionKey(HttpContext http) =>
        http.User.IsSignedIn() ? $"u:{http.User.Identity!.Name}" : $"ip:{ClientIp(http)}";

    /// <summary>Behind Render's proxy this is the forwarded client address (ASPNETCORE_FORWARDEDHEADERS_ENABLED).</summary>
    internal static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    internal static async ValueTask OnRejectedAsync(OnRejectedContext ctx, CancellationToken ct)
    {
        // Token bucket and window limiters know when a permit frees up; the concurrency queue doesn't, so say 1 s.
        var retryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
            ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds))
            : 1;
        var http = ctx.HttpContext;
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests",
                detail: $"Slow down and retry in {retryAfter} s.")
            .ExecuteAsync(http);
    }
}
