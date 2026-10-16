using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;

namespace Desk.Api.Positions;

/// <summary>
/// Serialized grid blocks, keyed by the normalized query (README §6 P1 caching). Size-limited by bytes, so a burst
/// of distinct queries evicts old blocks instead of growing the free instance's 512 MB.
/// </summary>
/// <remarks>
/// Entries expire at <see cref="MetaSnapshot.BatchEndsAt"/>, an instant computed on the app's
/// <see cref="TimeProvider"/>, so the cache must judge expiry on that same clock. A cache on the wall clock drops
/// every entry on insert whenever the injected clock runs behind it (a test's fixed fake time, once the real date
/// passes its batch end).
/// </remarks>
public sealed class PositionsCache : IDisposable
{
    public const string SizeConfigKey = "POSITIONS_CACHE_MB";

    /// <summary>
    /// The default limit. Full, under k6 load, the 512 MiB instance peaked at 338 MiB (ADR-0014); a demo session
    /// stores about 1.2 MB, so it holds dozens of sessions' first views until the batch.
    /// </summary>
    public const int DefaultMb = 64;

    /// <summary>
    /// The most the setting may ask for: at 96 MB the same load peaked at 389 MiB (76% of 512), at 128 MB at 422 MiB
    /// (82%), too close to the instance limit to leave room for exports and the insights fan-out (ADR-0014).
    /// </summary>
    public const int MaxMb = 96;

    public PositionsCache(IConfiguration config, TimeProvider time)
    {
        var mb = Math.Min(int.TryParse(config[SizeConfigKey], out var v) && v > 0 ? v : DefaultMb, MaxMb);
        Cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = mb * 1024L * 1024L, Clock = new Clock(time) });
        SizeLimitBytes = mb * 1024L * 1024L;
    }

    public MemoryCache Cache { get; }
    public long SizeLimitBytes { get; }

    public void Clear() => Cache.Clear();

    public void Dispose() => Cache.Dispose();

    /// <summary>MemoryCache's clock hook (it predates <see cref="TimeProvider"/>), reading the app's clock.</summary>
    private sealed class Clock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
