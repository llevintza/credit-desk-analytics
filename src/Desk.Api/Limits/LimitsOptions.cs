namespace Desk.Api.Limits;

/// <summary>README §7.2 limits. Environment variables override the defaults; there is no "off" switch.</summary>
public sealed record LimitsOptions(int PerUserPerMinute, int PerUserBurst, int LoginPerIpPerMinute, int GlobalConcurrency, int GlobalQueue)
{
    public static LimitsOptions From(IConfiguration config) => new(
        PerUserPerMinute: Positive(config, "RATE_LIMIT_PER_USER_PER_MIN", 60),
        PerUserBurst: Positive(config, "RATE_LIMIT_PER_USER_BURST", 20),
        LoginPerIpPerMinute: Positive(config, "RATE_LIMIT_LOGIN_PER_IP_PER_MIN", 5),
        GlobalConcurrency: Positive(config, "RATE_LIMIT_GLOBAL_CONCURRENCY", 8),
        GlobalQueue: Positive(config, "RATE_LIMIT_GLOBAL_QUEUE", 32));

    // A typo or 0 must not disable a limit: anything that isn't a positive integer falls back to the default.
    private static int Positive(IConfiguration config, string key, int fallback) =>
        int.TryParse(config[key], out var v) && v > 0 ? v : fallback;
}
