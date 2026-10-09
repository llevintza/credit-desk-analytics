namespace Desk.Data.Funds;

public enum RangeKind { QTD, YTD, OneYear, ITD, Custom }

/// <summary>
/// The month-ends a P2 range covers (README §6 P2), anchored on the fund's last available month-end (not the
/// calendar: the data runs through the month before the as-of date). Bounds are inclusive month-ends.
/// </summary>
public static class PerformanceRange
{
    /// <summary>Parses <c>range</c> (QTD | YTD | 1Y | ITD | CUSTOM, case-insensitive); null when unknown.</summary>
    public static RangeKind? Parse(string? range) => range?.ToUpperInvariant() switch
    {
        "QTD" => RangeKind.QTD,
        "YTD" => RangeKind.YTD,
        "1Y" => RangeKind.OneYear,
        "ITD" => RangeKind.ITD,
        "CUSTOM" => RangeKind.Custom,
        _ => null,
    };

    public static DateOnly MonthEnd(DateOnly d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    /// <summary>
    /// [from, to] for a preset range given the fund's first and last month-ends. ITD starts at the first
    /// month-end on or after inception, so a mid-month inception starts at that month's end.
    /// </summary>
    public static (DateOnly From, DateOnly To) Resolve(RangeKind kind, DateOnly first, DateOnly last, DateOnly? from = null, DateOnly? to = null)
    {
        var (start, end) = kind switch
        {
            RangeKind.QTD => (MonthEnd(new DateOnly(last.Year, (last.Month - 1) / 3 * 3 + 1, 1)), last),
            RangeKind.YTD => (MonthEnd(new DateOnly(last.Year, 1, 1)), last),
            RangeKind.OneYear => (MonthEnd(last.AddMonths(-11)), last),
            RangeKind.ITD => (first, last),
            _ => (MonthEnd(from!.Value), MonthEnd(to!.Value)),
        };
        // Never before the fund existed or after the data ends.
        return (start < first ? first : start, end > last ? last : end);
    }
}
