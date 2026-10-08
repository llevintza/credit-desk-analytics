using System.Globalization;

namespace Desk.Seeder;

/// <param name="MaxMegabytes">Committed-size budget (<c>--max-mb</c>): checked just before COMMIT, after the load.</param>
/// <param name="CapMegabytes">Storage cap for the reseed peak (<c>--cap-mb</c>): checked before TRUNCATE against the
/// current size plus the new dataset estimate, because the old files stay until COMMIT (README §5.4, §10).</param>
public sealed record SeedOptions(int Seed, decimal Scale, bool IfChanged, bool Force, bool SizeReportOnly, long MaxMegabytes, DateOnly AsOf,
    long CapMegabytes = SeedOptions.DefaultCapMegabytes)
{
    /// <summary>The planning cap until the Neon project's real cap is confirmed (#109).</summary>
    public const long DefaultCapMegabytes = 512;

    public static SeedOptions Parse(string[] args) => Parse(args, TimeProvider.System);

    /// <summary>Parses the command line; the default <c>--as-of</c> comes from <paramref name="time"/>, so tests pin the date.</summary>
    public static SeedOptions Parse(string[] args, TimeProvider time)
    {
        var o = new SeedOptions(Seed: 42, Scale: 1.0m, IfChanged: false, Force: false, SizeReportOnly: false, MaxMegabytes: 400, AsOf: DefaultAsOf(time));
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            o = args[i] switch
            {
                "--seed" => o with { Seed = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--scale" => o with { Scale = decimal.Parse(Next(), CultureInfo.InvariantCulture) },
                "--if-changed" => o with { IfChanged = true },
                "--force" => o with { Force = true },
                "--size-report" => o with { SizeReportOnly = true },
                "--as-of" => o with { AsOf = ParseAsOf(Next()) },
                "--max-mb" => o with { MaxMegabytes = long.Parse(Next(), CultureInfo.InvariantCulture) },
                "--cap-mb" => o with { CapMegabytes = long.Parse(Next(), CultureInfo.InvariantCulture) },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'"),
            };
        }
        if (o.Scale is <= 0 or > 2) throw new ArgumentException("--scale must be in (0, 2]");
        if (o.CapMegabytes <= 0) throw new ArgumentException("--cap-mb must be a positive number of megabytes");
        if (o.IfChanged && o.Force) throw new ArgumentException("--if-changed and --force are mutually exclusive");
        // Be explicit about intent: a reseed truncates every seeded table (README §14.3).
        if (!o.IfChanged && !o.Force && !o.SizeReportOnly)
            throw new ArgumentException("choose a mode: --if-changed (skip when version/seed/scale already loaded), --force (always reseed) or --size-report");
        return o;
    }

    static DateOnly ParseAsOf(string value)
    {
        try { return DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture); }
        catch (FormatException e) { throw new FormatException($"--as-of must be yyyy-MM-dd: {e.Message}", e); }
    }

    /// <summary>The last completed business day: the overnight batch's as-of date.</summary>
    public static DateOnly DefaultAsOf(TimeProvider time) => Generation.Tables.PreviousBusinessDay(DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
}
