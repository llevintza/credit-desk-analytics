namespace Desk.Data.Funds;

/// <summary>One P2 row: a label, a display format and one value per month (README §6 P2).</summary>
public sealed record FundRow(string Label, string Format, decimal?[] Values);

public sealed record FundPerformance(int FundId, string FundName, string Range, DateOnly? From, DateOnly? To, string[] Months, FundRow[] Rows);

/// <summary>
/// Long rows → months + one array per measure, at the edge (ADR-0011). Every row must have exactly one value per
/// month: a mismatch is a bug, so it throws (the API turns it into a logged 500) instead of shipping a skewed grid.
/// </summary>
public static class FundPivot
{
    public static FundPerformance Pivot(FundSpan fund, string range, DateOnly? from, DateOnly? to, IReadOnlyList<FundMonth> months)
    {
        var result = new FundPerformance(
            fund.FundId, fund.Name, range, from, to,
            [.. months.Select(m => m.AsOfMonth.ToString("yyyy-MM-dd"))],
            [
                new FundRow("Balance", "money0", [.. months.Select(m => (decimal?)m.Balance)]),
                // IRR arrives as double precision; decimal keeps it exact on the wire (rounded only for display).
                new FundRow("IRR", "pct2", [.. months.Select(m => ToDecimal(m.IrrItd))]),
            ]);
        Validate(result);
        return result;
    }

    /// <summary>
    /// double → decimal without throwing: NaN, infinities and values beyond decimal's range (~7.9e28, e.g. an IRR
    /// annualised over a tiny first period) become null instead of failing the whole fund.
    /// </summary>
    internal static decimal? ToDecimal(double d) => double.IsFinite(d) && Math.Abs(d) < 7.9e28 ? (decimal)d : null;

    public static void Validate(FundPerformance p)
    {
        foreach (var row in p.Rows)
            if (row.Values.Length != p.Months.Length)
                throw new InvalidOperationException(
                    $"Fund {p.FundId} performance invariant violated: row '{row.Label}' has {row.Values.Length} values for {p.Months.Length} months.");
    }
}
