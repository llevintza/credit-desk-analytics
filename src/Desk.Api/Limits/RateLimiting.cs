using System.Globalization;
using System.Threading.RateLimiting;
using Desk.Api.Auth;
using Desk.Api.Marks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Desk.Api.Limits;

/// <summary>
/// Free-tier protection (README §7.2): per-user concurrency (one in flight, or five for insights source reads), a
/// per-user token bucket and one shared concurrency limiter on all of <c>/api</c> (every endpoint there can reach the
/// database), plus a per-IP window on login. "IP" is the client's
/// address as <see cref="ClientAddress"/> resolves it behind Cloudflare and Render, never a proxy's.
/// </summary>
public static class RateLimiting
{
    public const string LoginPolicy = "login";

    public static IServiceCollection AddDeskRateLimiting(this IServiceCollection services, LimitsOptions limits, ClientAddress clients, MarksOptions? marks = null)
    {
        marks ??= MarksOptions.From(new ConfigurationBuilder().Build());
        services.AddSingleton(limits);
        services.AddSingleton(clients);
        services.AddSingleton(marks);
        services.AddSingleton<ExportGate>();
        var permits = new DatabasePermits(limits);
        services.AddSingleton(_ => permits); // a factory, so the container disposes it
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
            // caller's turn holds no database permit, so one busy user can't fill the shared ones. The token comes
            // before the database permit, so a caller with none left gets its 429 at once instead of holding a place
            // in the shared queue. A token is never given back, so the chain is acquired once (AcquireOnce, #184).
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
            // export deadline. The insights source read gets its own partition per user (5 in flight, queue 15, #357):
            // the board asks for its five sources at once and must paint each as it lands (README §6 P3). Its key
            // differs from the user's main one, so the two share no permits or queue places: a slow source can't hold
            // the grid back, and a busy grid can't hold the board back. It is read-only and bounded, and it still
            // sits before the user's token bucket and the shared database limiter, like every other partition.
            // A signed-in caller without an account id is refused first, exports included (#361): see NoAccountIdKey.
            // The intraday mark stream (README §6 P6, ADR-0015) is open for minutes: in the main partition it would hold
            // its owner's one permit and stall their grid, so it gets its own, a few streams per user and no queue.
            var perUserConcurrency = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                !IsApi(http)
                    ? RateLimitPartition.GetNoLimiter("unscoped")
                    : http.User.HasNoAccountId()
                        ? RateLimitPartition.Get(NoAccountIdKey, _ => DenyAll.Instance)
                    : IsExport(http) || !http.User.IsSignedIn()
                        ? RateLimitPartition.GetNoLimiter("unscoped")
                    : IsMarkStream(http)
                        ? RateLimitPartition.GetConcurrencyLimiter(MarkStreamPartitionKey(http, clients), _ => new ConcurrencyLimiterOptions
                        {
                            PermitLimit = marks.PerUserStreams,
                            QueueLimit = 0,
                        })
                    : IsInsightsSource(http)
                        ? RateLimitPartition.GetConcurrencyLimiter(InsightsPartitionKey(http, clients), _ => new ConcurrencyLimiterOptions
                        {
                            PermitLimit = limits.InsightsConcurrency,
                            QueueLimit = limits.InsightsQueue,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        })
                        : RateLimitPartition.GetConcurrencyLimiter(PartitionKey(http, clients), _ => new ConcurrencyLimiterOptions
                        {
                            PermitLimit = limits.PerUserConcurrency,
                            QueueLimit = limits.PerUserQueue,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        }));
            // One limiter instance for the partition and for handlers that take another permit (DatabasePermits). A mark
            // stream holds no database permit while it streams (it reads nothing then): it takes one from DatabasePermits
            // for its start-up read only, and its slot here is a cap on open streams instead.
            var database = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                !IsApi(http)
                    ? RateLimitPartition.GetNoLimiter("static")
                    : IsMarkStream(http)
                        ? RateLimitPartition.GetConcurrencyLimiter("marks", _ => new ConcurrencyLimiterOptions
                        {
                            PermitLimit = marks.GlobalStreams,
                            QueueLimit = 0,
                        })
                        : RateLimitPartition.Get("db", _ => permits.Shared));
            o.GlobalLimiter = new AcquireOnce(PartitionedRateLimiter.CreateChained(perUserConcurrency, perUser, database));

            o.AddPolicy(LoginPolicy, http => RateLimitPartition.GetFixedWindowLimiter(clients.For(http), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.LoginPerIpPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
        });
        return services;
    }

    /// <summary>
    /// The middleware tries a synchronous acquire and, when that fails, acquires again asynchronously. A chain fails
    /// its synchronous attempt at the first busy limiter, after the earlier ones have granted, and a token bucket's
    /// token is gone even though its lease is released: a request that queues for a database permit would pay two
    /// tokens (#184). This refuses every synchronous attempt without touching the chain, so the asynchronous acquire
    /// is the only one. It completes synchronously whenever the limiters have room, as the attempt would have.
    /// </summary>
    internal sealed class AcquireOnce(PartitionedRateLimiter<HttpContext> inner) : PartitionedRateLimiter<HttpContext>
    {
        public override RateLimiterStatistics? GetStatistics(HttpContext resource) => inner.GetStatistics(resource);

        protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount) => NotAcquired.Instance;

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext resource, int permitCount, CancellationToken cancellationToken) =>
            inner.AcquireAsync(resource, permitCount, cancellationToken);
    }

    /// <summary>
    /// The <see cref="NoAccountIdKey"/> partition: grants nothing, queues nothing (the built-in limiters need at least
    /// one permit). Its refusal goes to <see cref="OnRejectedAsync"/>, which answers 401.
    /// </summary>
    internal sealed class DenyAll : RateLimiter
    {
        public static readonly DenyAll Instance = new();

        private DenyAll() { }

        // Never idle, so the partitioned limiter keeps the one instance instead of disposing it.
        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics GetStatistics() => new();

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => NotAcquired.Instance;

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RateLimitLease>(NotAcquired.Instance);
    }

    /// <summary>A refusal with no Retry-After: <see cref="AcquireOnce"/>'s skipped attempt, and every <see cref="DenyAll"/> answer.</summary>
    internal sealed class NotAcquired : RateLimitLease
    {
        public static readonly NotAcquired Instance = new();

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private static bool IsApi(HttpContext http) => http.Request.Path.StartsWithSegments("/api");

    // UseRouting runs before the limiter, so the matched endpoint is known here.
    internal static bool IsExport(HttpContext http) => http.GetEndpoint()?.Metadata.GetMetadata<ExportEndpoint>() is not null;

    internal static bool IsMarkStream(HttpContext http) => http.GetEndpoint()?.Metadata.GetMetadata<MarkStreamEndpoint>() is not null;

    /// <summary>The caller's mark-stream partition; the prefix keeps it apart from every other partition, like <see cref="InsightsPartitionKey"/>.</summary>
    internal static string MarkStreamPartitionKey(HttpContext http, ClientAddress clients) => $"marks:{PartitionKey(http, clients)}";

    internal static bool IsInsightsSource(HttpContext http) => http.GetEndpoint()?.Metadata.GetMetadata<InsightsSourceEndpoint>() is not null;

    /// <summary>
    /// The caller's insights partition. Every <see cref="PartitionKey"/> starts with <c>u:</c>, <c>ip:</c> or <c>deny:</c>, so this
    /// prefix can't collide with any caller's main partition, whatever a user name contains.
    /// </summary>
    internal static string InsightsPartitionKey(HttpContext http, ClientAddress clients) => $"insights:{PartitionKey(http, clients)}";

    /// <summary>
    /// The partition of a signed-in caller without an account id (#361). Keyed on the client address, everyone behind
    /// one address would share it and one in-flight permit (the #165 hazard); keyed on the user name, a rename would
    /// make a second user (#185). It shares nothing with any <c>u:</c> or <c>ip:</c> partition and grants nothing.
    /// </summary>
    internal const string NoAccountIdKey = "deny:no-account-id";

    /// <summary>
    /// Signed-in users get their own bucket, keyed on the account id (#185); anonymous callers get one per client IP;
    /// a signed-in caller without an id gets <see cref="NoAccountIdKey"/>, never the IP.
    /// </summary>
    internal static string PartitionKey(HttpContext http, ClientAddress clients) =>
        !http.User.IsSignedIn() ? $"ip:{clients.For(http)}"
        : http.User.TryGetAccountId(out var id) ? $"u:{id}"
        : NoAccountIdKey;

    internal static async ValueTask OnRejectedAsync(OnRejectedContext ctx, CancellationToken ct)
    {
        // Token bucket and window limiters know when a permit frees up; the concurrency queue doesn't, so say 1 s.
        var retryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
            ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds))
            : 1;
        var http = ctx.HttpContext;
        // Refused for having no account id, not for going too fast: a 401 to sign in again, with no Retry-After.
        if (http.User.HasNoAccountId())
        {
            await PrincipalExtensions.NoAccountId().ExecuteAsync(http);
            return;
        }
        // A signed-in caller is keyed on the user, not the client address: their 429 (their own queue, their bucket)
        // says nothing about the resolver and must not use up its one-shot line (#165).
        if (!http.User.IsSignedIn())
            http.RequestServices.GetRequiredService<ClientAddressDiagnostics>().Rejected(http);
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests",
                detail: $"Slow down and retry in {retryAfter} s.")
            .ExecuteAsync(http);
    }
}
