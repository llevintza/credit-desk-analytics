using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Desk.Data.App.Migrations;
using Desk.Data.Catalog;
using Desk.Seeder.Generation;
using Desk.Seeder;

namespace Desk.Seeder.Tests;

/// <summary>Pure generator tests: no database.</summary>
public sealed class GeneratorTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 6);

    [Fact]
    public void Catalog_has_at_least_200_unique_columns()
    {
        Assert.True(ColumnCatalog.PositionSnapshot.Count >= 200, $"only {ColumnCatalog.PositionSnapshot.Count} columns");
        Assert.Equal(ColumnCatalog.PositionSnapshot.Count, ColumnCatalog.Names.Count);
        Assert.Equal(100, ColumnCatalog.PositionSnapshot.Count(c => c.Name.StartsWith("scn_", StringComparison.Ordinal)));
    }

    [Fact]
    public void Migration_snapshot_ddl_is_pinned_to_the_catalog()
    {
        // Changing ColumnCatalog without a new migration would make the table and the API whitelist disagree.
        static string Norm(string s) => string.Join('\n', s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        Assert.Contains(Norm(ColumnCatalog.PositionSnapshotDdl()), Norm(DataSchemas.SchemasSql));
    }

    [Theory]
        [InlineData("03783310", '0')] // published check-digit vectors (digits only; not an issuer name)
    [InlineData("38259P50", '8')]
    [InlineData("594918BW", '3')]
    public void Cusip_check_digit_matches_the_standard_algorithm(string first8, char expected) =>
        Assert.Equal(expected, Universe.CusipCheckDigit(first8));

    [Fact]
    public void Synthetic_cusips_are_unique_and_self_consistent()
    {
        var cusips = Enumerable.Range(1, 5000).Select(Universe.Cusip).ToList();
        Assert.Equal(cusips.Count, cusips.Distinct().Count());
        Assert.All(cusips, c => Assert.Equal(c[8], Universe.CusipCheckDigit(c[..8])));
    }

    [Fact]
    public void Rng_algorithm_is_pinned()
    {
        // If these change, every seeded value changes: bump SeedVersion deliberately, then update the pins.
        var r = new Rng(42);
        Assert.Equal(1546998764402558742UL, r.NextULong());
        Assert.Equal(6990951692964543102UL, r.NextULong());
        Assert.Equal(12544586762248559009UL, r.NextULong());
        Assert.Equal(2384964824180469153UL, Rng.For(42, "positions").NextULong());
    }

    [Fact]
    public void Same_seed_and_as_of_generate_identical_data_and_another_seed_does_not()
    {
        Assert.Equal(Fingerprint(42), Fingerprint(42));
        Assert.NotEqual(Fingerprint(42), Fingerprint(43));
    }

    [Fact]
    public void Edge_cases_from_the_spec_are_generated()
    {
        var u = Universe.Generate(42, 0.1, AsOf);
        var dealsWithBonds = u.Bonds.Select(b => b.DealId).ToHashSet();
        Assert.True(u.Deals.Count(d => !dealsWithBonds.Contains(d.DealId)) >= 3, "deals with zero bonds");
        Assert.Contains(u.Bonds.GroupBy(b => b.DealId), g => g.Count() == 1);
        Assert.Contains(u.Bonds, b => b.CouponOrMargin is null);
        Assert.Contains(u.Funds, f => f.InceptionDate.Day != DateTime.DaysInMonth(f.InceptionDate.Year, f.InceptionDate.Month));
        Assert.Contains(new Tables(42, 0.1, AsOf, u).FundFlowRows().GroupBy(r => ((int)r[1]!, (DateOnly)r[2]!)), g => g.Count() > 1);
    }

    [Fact]
    public void Scenario_prices_fall_as_spreads_widen()
    {
        var u = Universe.Generate(42, 0.05, AsOf);
        var t = new Tables(42, 0.05, AsOf, u);
        foreach (var a in t.BondAnalytics.Values.Take(200))
        {
            var tight = Analytics.ScenarioPrice(a, 0, -100);
            var wide = Analytics.ScenarioPrice(a, 0, 500);
            Assert.True(wide <= tight, $"price {a.Price}: wide {wide} > tight {tight}");
        }
    }

    [Fact]
    public void Running_without_a_mode_is_refused()
    {
        // A reseed truncates every seeded table, so the caller must say --if-changed or --force.
        var e = Assert.Throws<ArgumentException>(() => SeedOptions.Parse([]));
        Assert.Contains("--if-changed", e.Message);
        Assert.True(SeedOptions.Parse(["--if-changed"]).IfChanged);
        Assert.True(SeedOptions.Parse(["--force"]).Force);
        Assert.True(SeedOptions.Parse(["--size-report"]).SizeReportOnly);
    }

    [Fact]
    public void Generated_names_are_pinned_and_unique()
    {
        // Names are invented words (AGENTS.md: no real company names). Any change to the name set must be
        // reviewed deliberately: update this pin in the same PR.
        var u = Universe.Generate(42, 1.0, AsOf);
        var all = u.Issuers.Select(x => x.Name).Concat(u.Servicers.Select(x => x.Name)).Concat(u.Trustees.Select(x => x.Name)).ToList();
        Assert.Equal(93, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(PinnedNamesHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", all))))[..16]);
    }

    private const string PinnedNamesHash = "0EF87D67625EB0DD";

    [Theory]
    [InlineData(2026, 10, 6)]
    [InlineData(2026, 10, 30)]
    [InlineData(2026, 11, 27)]
    public void Month_end_trades_land_on_a_business_day_on_or_before_as_of(int y, int m, int d)
    {
        var asOf = new DateOnly(y, m, d);
        var u = Universe.Generate(42, 1.0, asOf);
        var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        foreach (var row in new Tables(42, 1.0, asOf, u).TradeRows())
        {
            var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime((DateTimeOffset)row[3]!, ny).DateTime);
            Assert.True(local <= asOf, $"trade {row[0]} on {local} after {asOf}");
            Assert.False(local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, $"trade {row[0]} on a weekend ({local})");
            var tod = TimeZoneInfo.ConvertTime((DateTimeOffset)row[3]!, ny).TimeOfDay;
            if (tod >= new TimeSpan(23, 59, 59))
            {
                var monthEnd = Tables.LastBusinessDayOfMonth(local.Year, local.Month);
                if (monthEnd > asOf) monthEnd = Tables.LastBusinessDayOfMonth(asOf.AddMonths(-1).Year, asOf.AddMonths(-1).Month);
                Assert.Equal(monthEnd, local);
            }
        }
    }

    [Fact]
    public void Catalog_and_seeded_table_identifiers_are_safe()
    {
        var rx = new Regex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant);
        foreach (var c in ColumnCatalog.PositionSnapshot)
            Assert.Matches(rx, c.Name);
        foreach (var table in Loader.SeededTables)
        foreach (var part in table.Split('.'))
            Assert.Matches(rx, part);
    }

    [Fact]
    public void Golden_hash_of_all_generated_tables_is_pinned_on_linux_x64()
    {
        var u = Universe.Generate(42, 0.05, AsOf);
        var t = new Tables(42, 0.05, AsOf, u);
        var sb = new StringBuilder();
        void Add(string name, IEnumerable<object?[]> rows)
        {
            sb.Append(name).Append('\n');
            foreach (var row in rows)
                sb.AppendJoin('|', row.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))).Append('\n');
        }
        Add("issuers", u.Issuers.Select(x => new object?[] { x.Id, x.Name, x.Country }));
        Add("servicers", u.Servicers.Select(x => new object?[] { x.Id, x.Name }));
        Add("trustees", u.Trustees.Select(x => new object?[] { x.Id, x.Name }));
        Add("deals", u.Deals.Select(d => new object?[] { d.DealId, d.Name, d.Sector }));
        Add("bonds", u.Bonds.Select(b => new object?[] { b.BondId, b.DealId, b.Cusip, b.CouponOrMargin }));
        Add("snapshot", t.PositionSnapshotRows(AsOf));
        Add("history", t.PositionHistoryRows());
        Add("trades", t.TradeRows());
        Add("fund-performance", t.FundPerformanceRows());
        Add("fund-flow", t.FundFlowRows());
        Add("rate-curve", t.RateCurveRows());
        Add("spread-index", t.SpreadIndexRows());
        Add("deal-remit", t.DealRemitRows());
        Add("vendor-mark", t.VendorMarkRows());
        Add("internal-mark", t.InternalMarkRows());
        Assert.Equal(GoldenHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16]);
    }

    private const string GoldenHash = "D41568039FF16054";

    private static string Fingerprint(int seed)
    {
        var u = Universe.Generate(seed, 0.05, AsOf);
        var t = new Tables(seed, 0.05, AsOf, u);
        var sb = new StringBuilder();
        foreach (var row in t.PositionSnapshotRows(AsOf).Concat(t.TradeRows()))
            sb.AppendJoin('|', row.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
