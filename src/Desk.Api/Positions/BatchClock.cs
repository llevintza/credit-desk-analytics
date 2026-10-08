namespace Desk.Api.Positions;

/// <summary>
/// The data changes once a day, when the overnight batch lands at 06:30 America/New_York (README §8). Cached
/// responses and reference data live until the next such instant.
/// </summary>
public static class BatchClock
{
    public static readonly TimeOnly BatchTime = new(6, 30);
    private static readonly TimeZoneInfo NewYork = Find("America/New_York");

    public static DateTimeOffset NextBatchAfter(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, NewYork);
        var candidate = local.Date + BatchTime.ToTimeSpan();
        if (candidate <= local.DateTime) candidate = candidate.AddDays(1);
        return new DateTimeOffset(candidate, NewYork.GetUtcOffset(candidate));
    }

    // tzdata ships in the runtime image; the fixed-offset fallback (EST) only matters on a host without it,
    // and then the batch time is off by at most an hour in summer.
    internal static TimeZoneInfo Find(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)
            ? tz
            : TimeZoneInfo.CreateCustomTimeZone("EST", TimeSpan.FromHours(-5), "EST", "EST");
}
