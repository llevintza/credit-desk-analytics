using System.Globalization;

namespace Desk.Seeder;

public sealed record SeedOptions(int Seed, decimal Scale, bool IfChanged, bool Force, bool SizeReportOnly, long MaxMegabytes, DateOnly AsOf)
{
    public static SeedOptions Parse(string[] args)
    {
        var o = new SeedOptions(Seed: 42, Scale: 1.0m, IfChanged: false, Force: false, SizeReportOnly: false, MaxMegabytes: 400, AsOf: DefaultAsOf());
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
                "--as-of" => o with { AsOf = DateOnly.ParseExact(Next(), "yyyy-MM-dd", CultureInfo.InvariantCulture) },
                "--max-mb" => o with { MaxMegabytes = long.Parse(Next(), CultureInfo.InvariantCulture) },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'"),
            };
        }
        if (o.Scale is <= 0 or > 2) throw new ArgumentException("--scale must be in (0, 2]");
        if (o.IfChanged && o.Force) throw new ArgumentException("--if-changed and --force are mutually exclusive");
        // Be explicit about intent: a reseed truncates every seeded table (README §14.3).
        if (!o.IfChanged && !o.Force && !o.SizeReportOnly)
            throw new ArgumentException("choose a mode: --if-changed (skip when version/seed/scale already loaded), --force (always reseed) or --size-report");
        return o;
    }

    /// <summary>The last completed business day: the overnight batch's as-of date.</summary>
    public static DateOnly DefaultAsOf() => Generation.Tables.PreviousBusinessDay(DateOnly.FromDateTime(DateTime.UtcNow));
}
