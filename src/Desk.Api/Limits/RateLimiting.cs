using System.Globalization;
using System.Threading.RateLimiting;
using Desk.Api.Auth;
using Microsoft.AspNetCore.RateLimiting;

namespace Desk.Api.Limits;

/// <summary>
/// Free-tier protection (README §7.2): a per-user token bucket on all of <c>/api</c>, a per-IP window on login,
/// and one shared concurrency limiter in front of every endpoint that can reach the database.
/// </summary>
public static class RateLimiting
{
    public const string LoginPolicy = "login";
    public const string DbPolicy = "db";

    public static IServiceCollection AddDeskRateLimiting(this IServiceCollection services, LimitsOptions limits)
    {
        services.AddSingleton(limits);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = OnRejectedAsync;

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                !http.Request.Path.StartsWithSegments("/api")
                    ? RateLimitPartition.GetNoLimiter("static")
                    : RateLimitPartition.GetTokenBucketLimiter(PartitionKey(http), _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = limits.PerUserBurst,
                        TokensPerPeriod = 1,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1) / limits.PerUserPerMinute,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));

            o.AddPolicy(LoginPolicy, http => RateLimitPartition.GetFixedWindowLimiter(ClientIp(http), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.LoginPerIpPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

            // One partition: the limit is global across users and endpoints, sized to the DB connection budget.
            o.AddConcurrencyLimiter(DbPolicy, c =>
            {
                c.PermitLimit = limits.GlobalConcurrency;
                c.QueueLimit = limits.GlobalQueue;
                c.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
        });
        return services;
    }

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
