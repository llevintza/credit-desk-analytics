using System.Globalization;
using System.Threading.RateLimiting;
using Desk.Api.Auth;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Desk.Api.Limits;

/// <summary>
/// Free-tier protection (README §7.2): a per-user token bucket and one shared concurrency limiter on all of
/// <c>/api</c> (every endpoint there can reach the database), plus a per-IP window on login. "IP" is the client's
/// address as <see cref="ClientAddress"/> resolves it behind Cloudflare and Render, never a proxy's.
/// </summary>
public static class RateLimiting
{
    public const string LoginPolicy = "login";

    public static IServiceCollection AddDeskRateLimiting(this IServiceCollection services, LimitsOptions limits, ClientAddress clients)
    {
        services.AddSingleton(limits);
        services.AddSingleton(clients);
        services.AddSingleton<ExportGate>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ClientAddressDiagnostics>();
        services.AddHostedService(sp => sp.GetRequiredService<ClientAddressDiagnostics>());
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = OnRejectedAsync;

            // Every /api request passes all three, in this order: the caller's concurrency (one in flight, a small
            // queue), the caller's token bucket, then one shared concurrency limiter sized to the DB connection budget.
            // Applying them to the whole group means no endpoint can forget to opt in. A request waiting for its
            // caller's turn holds no database permit, so one busy user can't fill the shared ones. Concurrency comes
            // first because a token is never given back: the middleware tries a synchronous acquire before it queues,
            // and a token spent on that failed attempt would charge a queued request twice.
            var perUser = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                !IsApi(http)
                    ? RateLimitPartition.GetNoLimiter("static")
                    : RateLimitPartition.GetTokenBucketLimiter(PartitionKey(http, clients), _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = limits.PerUserBurst,
                        TokensPerPeriod = 1,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1) / limits.PerUserPerMinute,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            // Signed-in users only. Anonymous callers (login, a signed-out /api/me) are already bounded by their
            // token bucket, the login window and the shared limiter; keyed per IP, one permit could be shared by
            // everyone whenever the client address resolves to one key. Exports are capped by ExportGate instead (one
            // per user, a few in total): counted here, a running export would block its owner's grid for up to the
            // export deadline.
            var perUserConcurrency = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                !IsApi(http) || IsExport(http) || !http.User.IsSignedIn()
                    ? RateLimitPartition.GetNoLimiter("unscoped")
                    : RateLimitPartition.GetConcurrencyLimiter(PartitionKey(http, clients), _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = limits.PerUserConcurrency,
                        QueueLimit = limits.PerUserQueue,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
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
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(perUserConcurrency, perUser, database);

            o.AddPolicy(LoginPolicy, http => RateLimitPartition.GetFixedWindowLimiter(clients.For(http), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.LoginPerIpPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
        });
        return services;
    }

    private static bool IsApi(HttpContext http) => http.Request.Path.StartsWithSegments("/api");

    // UseRouting runs before the limiter, so the matched endpoint is known here.
    internal static bool IsExport(HttpContext http) => http.GetEndpoint()?.Metadata.GetMetadata<ExportEndpoint>() is not null;

    /// <summary>Signed-in users get their own bucket; anonymous callers get one per client IP.</summary>
    internal static string PartitionKey(HttpContext http, ClientAddress clients) =>
        http.User.IsSignedIn() ? $"u:{http.User.Identity!.Name}" : $"ip:{clients.For(http)}";

    internal static async ValueTask OnRejectedAsync(OnRejectedContext ctx, CancellationToken ct)
    {
        // Token bucket and window limiters know when a permit frees up; the concurrency queue doesn't, so say 1 s.
        var retryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
            ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds))
            : 1;
        var http = ctx.HttpContext;
        http.RequestServices.GetRequiredService<ClientAddressDiagnostics>().Rejected(http);
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests",
                detail: $"Slow down and retry in {retryAfter} s.")
            .ExecuteAsync(http);
    }
}
