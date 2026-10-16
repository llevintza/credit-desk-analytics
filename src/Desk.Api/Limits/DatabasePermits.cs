using System.Threading.RateLimiting;

namespace Desk.Api.Limits;

/// <summary>
/// The shared database permits (README §7.2: <c>RATE_LIMIT_GLOBAL_CONCURRENCY</c>, queue <c>RATE_LIMIT_GLOBAL_QUEUE</c>).
/// The global limiter's one <c>db</c> partition is <see cref="Limiter"/>, and a request that holds no permit for its
/// whole life (the intraday mark stream, ADR-0015) takes one from <see cref="AcquireAsync"/> around its database read
/// instead, so every connection a request opens is still counted against the pool budget (#211).
/// </summary>
public sealed class DatabasePermits
{
    public DatabasePermits(LimitsOptions limits) => Limiter = new NeverIdle(new ConcurrencyLimiter(new ConcurrencyLimiterOptions
    {
        PermitLimit = limits.GlobalConcurrency,
        QueueLimit = limits.GlobalQueue,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    }));

    /// <summary>The permits themselves. Never idle, so the partitioned limiter keeps (and never disposes) this one instance.</summary>
    internal RateLimiter Limiter { get; }

    /// <summary>One permit, queued like any request's; a lease that isn't acquired means the queue was full (answer 429).</summary>
    public ValueTask<RateLimitLease> AcquireAsync(CancellationToken ct) => Limiter.AcquireAsync(1, ct);

    /// <summary>
    /// Forwards to the shared limiter but never reports idle: the partitioned limiter disposes a partition's limiter
    /// after it has been idle a while and builds a new one, which would split the shared permits in two.
    /// </summary>
    private sealed class NeverIdle(RateLimiter inner) : RateLimiter
    {
        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics? GetStatistics() => inner.GetStatistics();

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => inner.AttemptAcquire(permitCount);

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            inner.AcquireAsync(permitCount, cancellationToken);
    }
}

/// <summary>
/// Marks the intraday mark stream (README §6 P6, ADR-0015): its own per-user partition and a global cap on open streams
/// instead of a database permit (<see cref="RateLimiting"/>). Matched on the routed endpoint, like <see cref="ExportEndpoint"/>.
/// </summary>
public sealed class MarkStreamEndpoint
{
    public static readonly MarkStreamEndpoint Instance = new();

    private MarkStreamEndpoint() { }
}
