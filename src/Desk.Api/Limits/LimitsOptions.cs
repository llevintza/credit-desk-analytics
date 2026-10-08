using System.Globalization;

namespace Desk.Api.Limits;

/// <summary>README §7.2 limits. Environment variables override the defaults; there is no "off" switch.</summary>
/// <param name="PerUserConcurrency">Requests one caller may have in flight at once (exports are counted separately).</param>
/// <param name="PerUserQueue">Requests of one caller that wait for <paramref name="PerUserConcurrency"/> before a 429.</param>
/// <param name="ExportSlots">Exports running at once across all users; kept below the database permits.</param>
/// <param name="ExportTimeout">The overall deadline on one export stream.</param>
public sealed record LimitsOptions(
    int PerUserPerMinute, int PerUserBurst, int LoginPerIpPerMinute, int GlobalConcurrency, int GlobalQueue,
    int PerUserConcurrency, int PerUserQueue, int ExportSlots, TimeSpan ExportTimeout)
{
    public static LimitsOptions From(IConfiguration config) => new(
        PerUserPerMinute: Positive(config, "RATE_LIMIT_PER_USER_PER_MIN", 60),
        PerUserBurst: Positive(config, "RATE_LIMIT_PER_USER_BURST", 20),
        LoginPerIpPerMinute: Positive(config, "RATE_LIMIT_LOGIN_PER_IP_PER_MIN", 5),
        GlobalConcurrency: Positive(config, "RATE_LIMIT_GLOBAL_CONCURRENCY", 8),
        GlobalQueue: Positive(config, "RATE_LIMIT_GLOBAL_QUEUE", 32),
        PerUserConcurrency: Positive(config, "RATE_LIMIT_PER_USER_CONCURRENCY", 1),
        // The SPA's first paint fans out ~5 requests at once (session, portfolios, presets, first blocks): they queue
        // behind one another instead of failing.
        PerUserQueue: Positive(config, "RATE_LIMIT_PER_USER_QUEUE", 8),
        ExportSlots: Positive(config, "EXPORT_GLOBAL_SLOTS", 2),
        ExportTimeout: TimeSpan.FromSeconds(PositiveSeconds(config, "EXPORT_TIMEOUT_SECONDS", 60)));

    // A typo or 0 must not disable a limit: anything that isn't a positive integer falls back to the default.
    private static int Positive(IConfiguration config, string key, int fallback) =>
        int.TryParse(config[key], out var v) && v > 0 ? v : fallback;

    // Seconds may be fractional (tests use a short deadline); the same rule: not positive → the default.
    private static double PositiveSeconds(IConfiguration config, string key, double fallback) =>
        double.TryParse(config[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 && double.IsFinite(v) ? v : fallback;
}
