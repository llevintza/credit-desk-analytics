namespace Desk.Seeder.Tests;

public sealed class SeedOptionsTests
{
    [Fact]
    public void Parse_requires_a_mode()
    {
        var ex = Assert.Throws<ArgumentException>(() => SeedOptions.Parse([]));
        Assert.Contains("--if-changed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_defaults_with_if_changed()
    {
        var o = SeedOptions.Parse(["--if-changed"]);
        Assert.Equal(42, o.Seed);
        Assert.Equal(1.0m, o.Scale);
        Assert.True(o.IfChanged);
        Assert.False(o.Force);
        Assert.False(o.SizeReportOnly);
        Assert.Equal(400, o.MaxMegabytes);
        Assert.Equal(512, o.CapMegabytes);
        Assert.False(o.AsOf.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    [Fact]
    public void Parse_all_flags()
    {
        var o = SeedOptions.Parse(["--seed", "7", "--scale", "0.1", "--if-changed", "--size-report", "--max-mb", "50", "--as-of", "2026-10-06"]);
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
        var o = SeedOptions.Parse(["--force", "--scale", "2"]);
        Assert.True(o.Force);
        Assert.Equal(2m, o.Scale);
    }

    [Fact]
    public void Parse_as_of_rejects_bad_date()
    {
        var ex = Assert.Throws<FormatException>(() => SeedOptions.Parse(["--if-changed", "--as-of", "10/06/2026"]));
        Assert.Contains("--as-of", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2.1")]
    public void Parse_rejects_scale_outside_range(string scale)
    {
        var ex = Assert.Throws<ArgumentException>(() => SeedOptions.Parse(["--if-changed", "--scale", scale]));
        Assert.Contains("--scale", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_if_changed_with_force()
    {
        var ex = Assert.Throws<ArgumentException>(() => SeedOptions.Parse(["--if-changed", "--force"]));
        Assert.Contains("mutually exclusive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_unknown_option()
    {
        var ex = Assert.Throws<ArgumentException>(() => SeedOptions.Parse(["--drop"]));
        Assert.Contains("--drop", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_flag_missing_value()
    {
        var ex = Assert.Throws<ArgumentException>(() => SeedOptions.Parse(["--seed"]));
        Assert.Contains("needs a value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_cap_mb_is_separate_from_max_mb()
    {
        var o = SeedOptions.Parse(["--force", "--cap-mb", "1024", "--max-mb", "300"]);
        Assert.Equal(1024, o.CapMegabytes);
        Assert.Equal(300, o.MaxMegabytes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Parse_rejects_non_positive_cap_mb(string cap)
    {
        var ex = Assert.Throws<ArgumentException>(() => SeedOptions.Parse(["--force", "--cap-mb", cap]));
        Assert.Contains("--cap-mb", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_cap_mb_needs_a_number() =>
        Assert.Throws<FormatException>(() => SeedOptions.Parse(["--force", "--cap-mb", "lots"]));
}
