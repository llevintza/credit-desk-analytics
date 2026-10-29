namespace Desk.Mcp;

/// <summary>
/// Client-side limits (ADR-0031 §6): one tool call at a time (the per-account limiter allows one request in flight),
/// and a token bucket of <see cref="PerMinute"/> requests a minute, half the per-account 60/min, shared with the SPA.
/// </summary>
internal sealed class RequestGate(TimeProvider time, int perMinute = RequestGate.PerMinute)
{
    public const int PerMinute = 30;

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lock _bucket = new();
    private double _tokens = perMinute;
    private long _filled = time.GetTimestamp();

    /// <summary>Waits for the single call slot; dispose the result to release it.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        return new Release(_one);
    }

    /// <summary>Takes one request from the bucket, waiting for it to refill when it is empty.</summary>
    public async Task TakeAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_bucket)
            {
                var now = time.GetTimestamp();
                _tokens = Math.Min(perMinute, _tokens + time.GetElapsedTime(_filled, now).TotalMinutes * perMinute);
                _filled = now;
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return;
                }
                wait = TimeSpan.FromMinutes((1 - _tokens) / perMinute);
            }
            await Task.Delay(wait, time, ct);
        }
    }

    private sealed class Release(SemaphoreSlim one) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) one.Release();
        }
    }
}
