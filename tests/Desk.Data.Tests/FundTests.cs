using Desk.Data.Funds;

namespace Desk.Data.Tests;

/// <summary>README §6 P2 range rules and the pivot invariant.</summary>
public sealed class FundTests
{
    private static readonly DateOnly First = new(2023, 3, 31); // mid-month inception (2023-03-15) → first month-end
    private static readonly DateOnly Last = new(2026, 9, 30);

    private static int Months((DateOnly From, DateOnly To) r) => (r.To.Year - r.From.Year) * 12 + r.To.Month - r.From.Month + 1;

    [Theory]
    [InlineData("QTD", RangeKind.QTD)]
    [InlineData("ytd", RangeKind.YTD)]
    [InlineData("1Y", RangeKind.OneYear)]
    [InlineData("Itd", RangeKind.ITD)]
    [InlineData("CUSTOM", RangeKind.Custom)]
    public void Parses_ranges(string text, RangeKind kind) => Assert.Equal(kind, PerformanceRange.Parse(text));

    [Theory]
    [InlineData("MTD")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_ranges_are_null(string? text) => Assert.Null(PerformanceRange.Parse(text));

    [Fact]
    public void Preset_ranges_anchor_on_the_last_month_end()
    {
        Assert.Equal((new DateOnly(2026, 7, 31), Last), PerformanceRange.Resolve(RangeKind.QTD, First, Last));
        Assert.Equal(3, Months(PerformanceRange.Resolve(RangeKind.QTD, First, Last)));
        Assert.Equal((new DateOnly(2026, 1, 31), Last), PerformanceRange.Resolve(RangeKind.YTD, First, Last));
        Assert.Equal(9, Months(PerformanceRange.Resolve(RangeKind.YTD, First, Last)));
        Assert.Equal((new DateOnly(2025, 10, 31), Last), PerformanceRange.Resolve(RangeKind.OneYear, First, Last));
        Assert.Equal(12, Months(PerformanceRange.Resolve(RangeKind.OneYear, First, Last)));
        Assert.Equal((First, Last), PerformanceRange.Resolve(RangeKind.ITD, First, Last));
        Assert.Equal(43, Months(PerformanceRange.Resolve(RangeKind.ITD, First, Last)));
    }

    [Theory]
    [InlineData(2026, 3, 31, 1, 31)]   // Q1 end → Jan..Mar
    [InlineData(2026, 12, 31, 10, 31)] // Q4 end → Oct..Dec
    [InlineData(2026, 4, 30, 4, 30)]   // first month of a quarter → just that month
    public void Qtd_starts_at_the_quarter(int y, int m, int d, int startMonth, int startDay)
    {
        var last = new DateOnly(y, m, d);
        Assert.Equal(new DateOnly(y, startMonth, startDay), PerformanceRange.Resolve(RangeKind.QTD, new DateOnly(2020, 1, 31), last).From);
    }

    [Fact]
    public void Ranges_never_start_before_inception_or_end_after_the_data()
    {
        var young = new DateOnly(2026, 6, 30);
        Assert.Equal((young, Last), PerformanceRange.Resolve(RangeKind.OneYear, young, Last));
        Assert.Equal((young, Last), PerformanceRange.Resolve(RangeKind.YTD, young, Last));
        // CUSTOM bounds are normalised to month-ends and clamped.
        Assert.Equal((new DateOnly(2024, 1, 31), new DateOnly(2024, 6, 30)),
            PerformanceRange.Resolve(RangeKind.Custom, First, Last, new DateOnly(2024, 1, 5), new DateOnly(2024, 6, 1)));
        Assert.Equal((First, Last),
            PerformanceRange.Resolve(RangeKind.Custom, First, Last, new DateOnly(2000, 1, 1), new DateOnly(2030, 1, 1)));
        // Entirely before inception: from > to, which the API turns into empty arrays.
        var (from, to) = PerformanceRange.Resolve(RangeKind.Custom, First, Last, new DateOnly(2020, 1, 1), new DateOnly(2020, 6, 1));
        Assert.True(from > to);
    }

    [Fact]
    public void Month_end_handles_leap_years()
    {
        Assert.Equal(new DateOnly(2024, 2, 29), PerformanceRange.MonthEnd(new DateOnly(2024, 2, 3)));
        Assert.Equal(new DateOnly(2026, 2, 28), PerformanceRange.MonthEnd(new DateOnly(2026, 2, 28)));
    }

    [Fact]
    public void Pivot_lines_up_one_value_per_month_and_keeps_money_exact()
    {
        var span = new FundSpan(4, "Residential Credit Fund", First, Last);
        var p = FundPivot.Pivot(span, "QTD", new DateOnly(2026, 7, 31), Last,
        [
            new FundMonth(new DateOnly(2026, 7, 31), 100_000_000.01m, 0.05),
            new FundMonth(new DateOnly(2026, 8, 31), 104_000_000.00m, double.NaN),
        ]);
        Assert.Equal(["2026-07-31", "2026-08-31"], p.Months);
        Assert.Equal([100_000_000.01m, 104_000_000.00m], p.Rows[0].Values);
        Assert.Equal(("Balance", "money0"), (p.Rows[0].Label, p.Rows[0].Format));
        Assert.Equal(("IRR", "pct2"), (p.Rows[1].Label, p.Rows[1].Format));
        Assert.Equal([0.05m, null], p.Rows[1].Values); // NaN never reaches the wire

        var empty = FundPivot.Pivot(span, "CUSTOM", null, null, []);
        Assert.Empty(empty.Months);
        Assert.All(empty.Rows, r => Assert.Empty(r.Values));
    }

    [Theory]
    [InlineData(0.05, true)]
    [InlineData(-1.5, true)]
    [InlineData(1e30, false)]   // beyond decimal: null, not an OverflowException that fails the whole fund
    [InlineData(-1e30, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void Irr_converts_to_decimal_or_null(double value, bool converts) =>
        Assert.Equal(converts, FundPivot.ToDecimal(value).HasValue);

    [Fact]
    public void A_row_that_does_not_line_up_with_the_months_is_an_invariant_violation()
    {
        var broken = new FundPerformance(1, "F", "YTD", null, null, ["2026-01-31", "2026-02-28"], [new FundRow("Balance", "money0", [1m])]);
        var ex = Assert.Throws<InvalidOperationException>(() => FundPivot.Validate(broken));
        Assert.Contains("invariant", ex.Message);
    }
}
