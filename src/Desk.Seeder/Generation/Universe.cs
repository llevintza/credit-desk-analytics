namespace Desk.Seeder.Generation;

public sealed record Fund(int FundId, string Name, DateOnly InceptionDate, string Strategy);
public sealed record Portfolio(int PortfolioId, int FundId, string Name, string Manager, string Benchmark, double Weight);
public sealed record Named(int Id, string Name, string? Country = null);

public sealed record Deal(
    int DealId, string Name, string Sector, string SubSector, int IssuerId, int ServicerId, int TrusteeId,
    int Vintage, DateOnly ClosingDate, string CollateralType, decimal OriginalBalance, string Status);

public sealed record Bond(
    int BondId, int DealId, string Cusip, string Class, int SeniorityRank, decimal OriginalBalance,
    decimal CurrentBalance, double Factor, string CouponType, double? CouponOrMargin, string? CouponIndex,
    string? RatingSp, string? RatingMoodys, string? RatingFitch, string RatingComposite, int RatingRank,
    DateOnly LegalFinal, DateOnly ExpectedMaturity, double AttachmentPct, double DetachmentPct);

/// <summary>
/// The fixed "world" every table is generated from: funds, portfolios, reference entities, deals and bonds.
/// All names are fictional (AGENTS.md: no real company or person names).
/// </summary>
public sealed class Universe
{
    public required IReadOnlyList<Fund> Funds { get; init; }
    public required IReadOnlyList<Portfolio> Portfolios { get; init; }
    public required IReadOnlyList<Named> Issuers { get; init; }
    public required IReadOnlyList<Named> Servicers { get; init; }
    public required IReadOnlyList<Named> Trustees { get; init; }
    public required IReadOnlyList<Deal> Deals { get; init; }
    public required IReadOnlyList<Bond> Bonds { get; init; }

    public static readonly IReadOnlyList<(string Sector, string SubSector, string Collateral, double Weight)> Sectors =
    [
        ("CLO", "BSL", "Broadly syndicated loans", 0.30), ("CLO", "Middle market", "Middle-market loans", 0.08),
        ("RMBS", "Prime", "Prime jumbo mortgages", 0.08), ("RMBS", "Non-QM", "Non-QM mortgages", 0.10),
        ("RMBS", "Legacy", "Pre-2008 mortgages", 0.04), ("CMBS", "Conduit", "Conduit commercial loans", 0.10),
        ("CMBS", "SASB", "Single-asset single-borrower", 0.05), ("ABS", "Auto", "Auto loans", 0.08),
        ("ABS", "Card", "Credit card receivables", 0.04), ("ABS", "Student", "Student loans", 0.03),
        ("ABS", "Equipment", "Equipment leases", 0.03), ("CRT", "Agency CRT", "GSE reference pool", 0.07),
    ];

    /// <summary>Composite ratings, best to worst, with the generic agency spellings.</summary>
    public static readonly IReadOnlyList<(string Composite, string Sp, string Moodys, string Fitch)> Ratings =
    [
        ("AAA", "AAA", "Aaa", "AAA"), ("AA", "AA", "Aa2", "AA"), ("A", "A", "A2", "A"), ("BBB", "BBB", "Baa2", "BBB"),
        ("BB", "BB", "Ba2", "BB"), ("B", "B", "B2", "B"), ("CCC", "CCC", "Caa2", "CCC"), ("NR", "NR", "NR", "NR"),
    ];

    private static readonly string[] NameA = ["Harbor", "Granite", "Juniper", "Summit", "Beacon", "Cedar", "Northgate", "Silverline", "Bluestone", "Ironwood", "Westbrook", "Lakeshore", "Redwood", "Fairhaven", "Kestrel", "Meridian", "Copperfield", "Stonebridge", "Windmere", "Highmark"];
    private static readonly string[] NameB = ["Point", "Ridge", "Crest", "Bay", "Hollow", "Field", "Park", "Rock", "Vale", "Gate"];
    private static readonly string[] Suffix = ["Capital", "Credit Partners", "Asset Management", "Funding", "Advisors", "Lending"];

    public static Universe Generate(int seed, double scale, DateOnly asOf)
    {
        var rng = Rng.For(seed, "universe");
        int Scaled(int n, int min) => Math.Max(min, (int)Math.Round(n * scale));

        var funds = new List<Fund>
        {
            new(1, "Structured Credit Opportunities Fund", new DateOnly(2021, 1, 31), "Opportunistic"),
            new(2, "Senior Secured ABS Fund", new DateOnly(2021, 6, 30), "Investment grade"),
            new(3, "CLO Debt & Equity Fund", new DateOnly(2022, 9, 30), "CLO"),
            // Mid-month inception (README §5.2 edge case): the first month-end row is 2023-03-31.
            new(4, "Residential Credit Fund", new DateOnly(2023, 3, 15), "Mortgage credit"),
        };

        var portfolios = new List<Portfolio>();
        string[] benchmarks = ["SOFR + 400", "Custom ABS index", "CLO BB index", "Agg securitized"];
        for (var i = 1; i <= 12; i++)
        {
            var fundId = (i - 1) % 4 + 1;
            // Uneven sizes: the first portfolio is the desk's flagship book (3,000+ lines at full scale).
            var weight = i == 1 ? 3.5 : rng.Uniform(0.4, 1.4);
            portfolios.Add(new Portfolio(i, fundId, $"Book {i:00}", $"pm{i:00}", benchmarks[fundId - 1], weight));
        }

        var issuers = Enumerable.Range(1, 60).Select(i =>
            new Named(i, $"{NameA[(i * 7) % NameA.Length]} {NameB[(i * 3) % NameB.Length]} {Suffix[i % Suffix.Length]}",
                i % 9 == 0 ? "GB" : "US")).ToList();
        var servicers = Enumerable.Range(1, 25).Select(i =>
            new Named(i, $"{NameA[(i * 11) % NameA.Length]} Servicing {(i % 3 == 0 ? "Corp." : "LLC")}")).ToList();
        var trustees = Enumerable.Range(1, 8).Select(i =>
            new Named(i, $"{NameA[(i * 5 + 3) % NameA.Length]} Trust Company")).ToList();

        var deals = new List<Deal>();
        var bonds = new List<Bond>();
        var dealCount = Scaled(1500, 20);
        var shelfCounter = new Dictionary<string, int>();
        var bondId = 0;

        for (var d = 1; d <= dealCount; d++)
        {
            var (sector, sub, collateral, _) = rng.Pick(Sectors.Select(s => (s, s.Weight)).ToList());
            var issuer = rng.Pick(issuers);
            var vintage = sector == "RMBS" && sub == "Legacy" ? rng.Int(2004, 2007) : rng.Int(2017, asOf.Year);
            var closing = new DateOnly(vintage, rng.Int(1, 12), rng.Int(1, 28));
            if (closing > asOf) closing = asOf.AddDays(-rng.Int(5, 60));
            var shelf = $"{Initials(issuer.Name)} {(sector == "CRT" ? "CRT" : sector)}";
            var key = $"{shelf} {vintage}";
            shelfCounter[key] = shelfCounter.GetValueOrDefault(key) + 1;
            var name = $"{key}-{shelfCounter[key]}";
            var original = Math.Round((decimal)rng.Uniform(250, 1200) * 1_000_000m, 0);

            // README §5.2 edge cases: the last three deals have no bonds yet; some deals have exactly one.
            var noBonds = d > dealCount - 3;
            var status = noBonds ? "Pricing pending" : vintage <= 2007 ? "Amortizing" : "Active";
            deals.Add(new Deal(d, name, sector, sub, issuer.Id, rng.Pick(servicers).Id, rng.Pick(trustees).Id,
                vintage, closing, collateral, original, status));
            if (noBonds) continue;

            var tranches = d % 50 == 7 ? 1 : sector switch
            {
                "CLO" => rng.Int(6, 9),
                "CMBS" => rng.Int(5, 12),
                "CRT" => rng.Int(3, 5),
                _ => rng.Int(3, 7),
            };
            foreach (var b in Tranches(rng, sector, tranches, closing, vintage, original, asOf))
            {
                bondId++;
                bonds.Add(b with { BondId = bondId, DealId = d, Cusip = Cusip(bondId) });
            }
        }

        return new Universe
        {
            Funds = funds, Portfolios = portfolios, Issuers = issuers, Servicers = servicers,
            Trustees = trustees, Deals = deals, Bonds = bonds,
        };
    }

    private static IEnumerable<Bond> Tranches(Rng rng, string sector, int count, DateOnly closing, int vintage,
        decimal dealBalance, DateOnly asOf)
    {
        // Capital structure from the top: the senior class takes ~60-70%, then thinner mezz slices, equity last.
        string[] classes = sector == "CLO"
            ? ["A1", "A2", "B", "C", "D", "E", "F", "SUB", "SUB2"]
            : ["A1", "A2", "A3", "M1", "M2", "B1", "B2", "B3", "C", "D", "E", "R"];
        var ageYears = Math.Max(0, asOf.Year - vintage);
        var dealFactor = Math.Clamp(1.0 - ageYears * rng.Uniform(0.06, 0.16), 0.05, 1.0);
        var attachment = 1.0;
        for (var i = 0; i < count; i++)
        {
            var isEquity = count > 1 && i == count - 1;
            var size = count == 1 ? 1.0 : i == 0 ? rng.Uniform(0.55, 0.68) : isEquity ? attachment : rng.Uniform(0.03, 0.09);
            size = Math.Min(size, attachment);
            var detach = attachment;
            attachment = Math.Max(0, attachment - size);

            var ratingIdx = isEquity ? 7 : Math.Min(6, i * 6 / Math.Max(1, count - 1) + (rng.Chance(0.1) ? 1 : 0));
            var r = Ratings[ratingIdx];
            // Junior classes pay down last: seniors carry most of the amortization.
            var factor = isEquity ? 1.0 : Math.Clamp(dealFactor * (i == 0 ? rng.Uniform(0.7, 0.95) : rng.Uniform(1.0, 1.15)), 0.0, 1.0);
            if (rng.Chance(0.01)) factor = 0.0; // fully paid down (README §5.2 edge case)
            var original = Math.Round(dealBalance * (decimal)size, 0);
            var floating = sector is "CLO" or "CRT" || rng.Chance(0.2);
            double? coupon = isEquity ? null : floating ? Math.Round(rng.Uniform(0.010, 0.012) + ratingIdx * 0.009, 4)
                                                        : Math.Round(rng.Uniform(0.03, 0.05) + ratingIdx * 0.006, 4);
            var legalFinal = closing.AddYears(sector == "CLO" ? 13 : sector == "CMBS" ? 30 : 25);
            var expected = closing.AddYears(sector == "CLO" ? rng.Int(6, 9) : rng.Int(4, 10));
            yield return new Bond(0, 0, "", classes[Math.Min(i, classes.Length - 1)], i + 1, original,
                Math.Round(original * (decimal)factor, 2), Math.Round(factor, 6),
                isEquity ? "RESIDUAL" : floating ? "FLOAT" : "FIXED", coupon, floating && !isEquity ? "SOFR" : null,
                isEquity ? null : r.Sp, isEquity ? null : r.Moodys, isEquity || rng.Chance(0.3) ? null : r.Fitch,
                r.Composite, ratingIdx, legalFinal, expected, Math.Round(attachment, 4), Math.Round(detach, 4));
        }
    }

    private static string Initials(string name) =>
        string.Concat(name.Split(' ').Take(2).Select(w => w[0])).ToUpperInvariant() + "X";

    /// <summary>Synthetic CUSIP: "9X" issuer prefix (not a real issuer block) + base-36 serial + valid check digit.</summary>
    public static string Cusip(int serial)
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var body = new char[8];
        body[0] = '9'; body[1] = 'X';
        var n = serial;
        for (var i = 7; i >= 2; i--) { body[i] = alphabet[n % 36]; n /= 36; }
        return new string(body) + CusipCheckDigit(new string(body));
    }

    public static char CusipCheckDigit(string first8)
    {
        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            var c = first8[i];
            var v = char.IsDigit(c) ? c - '0' : c - 'A' + 10;
            if (i % 2 == 1) v *= 2;
            sum += v / 10 + v % 10;
        }
        return (char)('0' + (10 - sum % 10) % 10);
    }
}
