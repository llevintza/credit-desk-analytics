using System.Security.Cryptography;
using System.Text;
using Desk.Data.App.Migrations;
using Desk.Data.Catalog;
using Desk.Seeder.Generation;

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
    [InlineData("03783310", '0')] // published CUSIPs, check digits from the issuers' prospectuses
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
