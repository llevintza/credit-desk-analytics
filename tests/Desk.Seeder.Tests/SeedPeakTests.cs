using Npgsql;

namespace Desk.Seeder.Tests;

/// <summary>Unit tests for the reseed peak estimate and the disk-full message (#109).</summary>
public sealed class SeedPeakTests
{
    const long Mb = 1024 * 1024;

    [Fact]
    public void A_full_scale_1_reseed_is_over_the_default_cap()
    {
        // Measured: a committed scale-1.0 book is ~271 MB and a --force peaked at 533.8 MB (README §5.4).
        var peak = SeedRunner.PeakEstimateMegabytes(271 * Mb, 1.0m);
        Assert.Equal(542, peak);
        Assert.True(peak > SeedOptions.DefaultCapMegabytes);
    }

    [Fact]
    public void A_first_seed_into_an_empty_database_fits_the_default_cap() =>
        Assert.True(SeedRunner.PeakEstimateMegabytes(8 * Mb, 1.0m) <= SeedOptions.DefaultCapMegabytes);

    [Fact]
    public void Peak_scales_the_new_data_half() =>
        Assert.Equal(10 + 27, SeedRunner.PeakEstimateMegabytes(10 * Mb, 0.1m));

    [Fact]
    public void Disk_full_maps_to_an_actionable_message()
    {
        var msg = SeedRunner.DescribeError(new PostgresException("could not extend file", "ERROR", "ERROR", "53100"));
        Assert.Contains("ran out of disk space", msg, StringComparison.Ordinal);
        Assert.Contains("53100", msg, StringComparison.Ordinal);
        Assert.Contains("SEED_PEAK_EST_MB", msg, StringComparison.Ordinal);
        Assert.Contains("rolled back", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void Disk_full_is_found_behind_a_wrapper()
    {
        var wrapped = new InvalidOperationException("load failed", new PostgresException("no space", "ERROR", "ERROR", "53100"));
        Assert.Contains("ran out of disk space", SeedRunner.DescribeError(wrapped), StringComparison.Ordinal);
    }

    [Fact]
    public void Other_postgres_errors_keep_the_generic_message()
    {
        var msg = SeedRunner.DescribeError(new PostgresException("duplicate key", "ERROR", "ERROR", "23505"));
        Assert.Equal("ERROR: PostgresException: 23505: duplicate key", msg);
    }

    [Fact]
    public void Non_database_errors_keep_the_generic_message() =>
        Assert.Equal("ERROR: TimeoutException: slow", SeedRunner.DescribeError(new TimeoutException("slow")));
}
