using System.Globalization;

namespace Desk.Api.Marks;

/// <summary>
/// The intraday overlay's settings (README §6 P6, §7.2; ADR-0015). Environment variables override the defaults; a
/// value that isn't a positive number, or is above its cap, keeps the default (like <see cref="Limits.LimitsOptions"/>),
/// so a typo can never make a stream unbounded.
/// </summary>
/// <param name="Tick">How often the simulated marks move: <c>MARKS_TICK_MS</c> (default 1,000, 50 to 60,000).</param>
/// <param name="Lifetime">
/// How long one stream stays open before the server ends it: <c>MARKS_STREAM_SECONDS</c> (default 300, at most 300,
/// the access token's lifetime). The client reconnects only while its user is active, so an open tab nobody looks at
/// stops holding the instance awake (ADR-0015 free-tier rule).
/// </param>
/// <param name="MaxBonds">Bonds one stream marks: <c>MARKS_MAX_BONDS</c> (default 100, at most 1,000).</param>
/// <param name="PerUserStreams">Streams one user may hold open: <c>MARKS_STREAMS_PER_USER</c> (default 2: a reload overlaps its old stream).</param>
/// <param name="GlobalStreams">Streams open at once across all users: <c>MARKS_STREAMS_GLOBAL</c> (default 16).</param>
/// <param name="TotalsBucket">
/// The overlay totals are computed at most once per this many seconds per view, and shared until the next bucket:
/// <c>MARKS_TOTALS_SECONDS</c> (default 15, at most 300).
/// </param>
public sealed record MarksOptions(TimeSpan Tick, TimeSpan Lifetime, int MaxBonds, int PerUserStreams, int GlobalStreams, TimeSpan TotalsBucket)
{
    public const double MaxLifetimeSeconds = 300;

    public static MarksOptions From(IConfiguration config) => new(
        Tick: TimeSpan.FromMilliseconds(Number(config, "MARKS_TICK_MS", 1_000, min: 50, max: 60_000)),
        Lifetime: TimeSpan.FromSeconds(Number(config, "MARKS_STREAM_SECONDS", MaxLifetimeSeconds, min: 0.1, max: MaxLifetimeSeconds)),
        MaxBonds: (int)Number(config, "MARKS_MAX_BONDS", 100, min: 1, max: 1_000),
        PerUserStreams: (int)Number(config, "MARKS_STREAMS_PER_USER", 2, min: 1, max: 10),
        GlobalStreams: (int)Number(config, "MARKS_STREAMS_GLOBAL", 16, min: 1, max: 1_000),
        TotalsBucket: TimeSpan.FromSeconds(Number(config, "MARKS_TOTALS_SECONDS", 15, min: 1, max: 300)));

    private static double Number(IConfiguration config, string key, double fallback, double min, double max) =>
        double.TryParse(config[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? (min >= 1 ? Math.Floor(v) : v)
            : fallback;
}
