namespace Desk.Seeder.Tests;

public sealed class SeedOptionsTests
{
    /// <summary>A Wednesday: the wall clock never decides a test's date or which branches run (#226).</summary>
    private static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    private static SeedOptions Parse(string[] args) => SeedOptions.Parse(args, Clock);

    [Fact]
    public void Parse_requires_a_mode()
    {
        var ex = Assert.Throws<ArgumentException>(() => Parse([]));
        Assert.Contains("--if-changed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_defaults_with_if_changed()
    {
        var o = Parse(["--if-changed"]);
        Assert.Equal(42, o.Seed);
        Assert.Equal(1.0m, o.Scale);
        Assert.True(o.IfChanged);
        Assert.False(o.Force);
        Assert.False(o.SizeReportOnly);
        Assert.Equal(400, o.MaxMegabytes);
        Assert.Equal(new DateOnly(2026, 10, 6), o.AsOf);
    }

    [Fact]
    public void Parse_all_flags()
    {
        var o = Parse(["--seed", "7", "--scale", "0.1", "--if-changed", "--size-report", "--max-mb", "50", "--as-of", "2026-10-06"]);
        Assert.Equal(7, o.Seed);
        Assert.Equal(0.1m, o.Scale);
        Assert.True(o.IfChanged);
        Assert.True(o.SizeReportOnly);
        Assert.Equal(50, o.MaxMegabytes);
        Assert.Equal(new DateOnly(2026, 10, 6), o.AsOf);
    }

    [Fact]
    public void Parse_force()
    {
        var o = Parse(["--force", "--scale", "2"]);
        Assert.True(o.Force);
        Assert.Equal(2m, o.Scale);
    }

    [Fact]
    public void Parse_as_of_rejects_bad_date()
    {
        var ex = Assert.Throws<FormatException>(() => Parse(["--if-changed", "--as-of", "10/06/2026"]));
        Assert.Contains("--as-of", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2.1")]
    public void Parse_rejects_scale_outside_range(string scale)
    {
        var ex = Assert.Throws<ArgumentException>(() => Parse(["--if-changed", "--scale", scale]));
        Assert.Contains("--scale", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_if_changed_with_force()
    {
        var ex = Assert.Throws<ArgumentException>(() => Parse(["--if-changed", "--force"]));
        Assert.Contains("mutually exclusive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_unknown_option()
    {
        var ex = Assert.Throws<ArgumentException>(() => Parse(["--drop"]));
        Assert.Contains("--drop", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_flag_missing_value()
    {
        var ex = Assert.Throws<ArgumentException>(() => Parse(["--seed"]));
        Assert.Contains("needs a value", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-10-05T00:00:00Z", "2026-10-02")] // Monday: back over the weekend to Friday
    [InlineData("2026-10-04T23:59:59Z", "2026-10-02")] // Sunday
    [InlineData("2026-10-03T12:00:00Z", "2026-10-02")] // Saturday
    [InlineData("2026-10-06T00:00:00Z", "2026-10-05")] // Tuesday
    [InlineData("2026-10-10T00:30:00+02:00", "2026-10-08")] // Friday 22:30 UTC: the UTC date counts, not the offset's
    public void Default_as_of_is_the_last_business_day_before_the_injected_clock(string now, string expected)
    {
        var clock = new FixedClock(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), SeedOptions.Parse(["--if-changed"], clock).AsOf);
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), SeedOptions.DefaultAsOf(clock));
    }

    [Fact]
    public void An_explicit_as_of_wins_over_the_clock()
    {
        Assert.Equal(new DateOnly(2026, 9, 30), Parse(["--if-changed", "--as-of", "2026-09-30"]).AsOf);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
