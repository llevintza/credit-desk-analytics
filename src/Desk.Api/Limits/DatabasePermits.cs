using System.Threading.RateLimiting;

namespace Desk.Api.Limits;

/// <summary>
/// The shared database permits (README §7.2): <see cref="LimitsOptions.GlobalConcurrency"/> permits, each worth one
/// pooled connection, so they fit <c>DB_MAX_POOL_SIZE</c> (<see cref="ConnectionBudget"/>). The rate limiter's
/// <c>db</c> partition hands one to every <c>/api</c> request. A handler that wants a second connection for the
/// same request (the positions grid's first view, #241) takes a <see cref="TryTakeSpare">spare</see> from the same
/// limiter, so a connection is never held without a permit.
/// </summary>
public sealed class DatabasePermits : IDisposable
{
    private readonly ConcurrencyLimiter _permits;

    public DatabasePermits(LimitsOptions limits)
    {
        _permits = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = limits.GlobalConcurrency,
            QueueLimit = limits.GlobalQueue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
        Shared = new NeverIdle(_permits);
    }

    /// <summary>The limiter behind the rate limiter's <c>db</c> partition.</summary>
    public RateLimiter Shared { get; }

    /// <summary>
    /// A second permit for a request that already holds one, or a lease that is not acquired. It never waits, so a
    /// request holding a permit can't block on another (no deadlock); it is refused while anyone is queued (the
    /// limiter serves its queue oldest first), and it never takes the last free permit, so a request that arrives
    /// next still finds one. Dispose the lease after the extra connection is back in the pool.
    /// </summary>
    public RateLimitLease TryTakeSpare() =>
        _permits.GetStatistics() is { CurrentAvailablePermits: >= 2, CurrentQueuedCount: 0 }
            ? _permits.AttemptAcquire(1)
            : RateLimiting.NotAcquired.Instance;

    public void Dispose() => _permits.Dispose();

    /// <summary>
    /// The partitioned limiter disposes and replaces a partition's limiter once it has been idle a while; this one
    /// is shared with <see cref="TryTakeSpare"/>, so it must stay the same instance. Its owner disposes it.
    /// </summary>
    private sealed class NeverIdle(ConcurrencyLimiter inner) : RateLimiter
    {
        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics? GetStatistics() => inner.GetStatistics();

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => inner.AttemptAcquire(permitCount);

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            inner.AcquireAsync(permitCount, cancellationToken);
    }
}
