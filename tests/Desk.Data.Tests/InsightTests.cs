using Desk.Data.Insights;
using Desk.Data.Sources;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Desk.Data.Tests;

/// <summary>README §6 P3: the grid catalog, the edge pivot and its invariant, cell safety, fan-out limits.</summary>
public sealed class InsightTests
{
    [Fact]
    public void Twenty_grids_across_five_sources_with_the_spec_split()
    {
        Assert.Equal(20, InsightCatalog.All.Count);
        Assert.Equal(["core", "market", "surveillance", "pricing", "reference"], InsightCatalog.Sources);
        Assert.Equal([6, 3, 5, 3, 3], InsightCatalog.Sources.Select(s => InsightCatalog.For(s).Count));
        Assert.Equal(InsightCatalog.All.Count, InsightCatalog.All.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Every_grid_is_scoped_to_the_callers_book_and_takes_only_the_two_parameters()
    {
        foreach (var spec in InsightCatalog.All)
        {
            Assert.Contains("portfolio_id = ANY(@portfolios)", spec.Sql);
            var parameters = System.Text.RegularExpressions.Regex.Matches(spec.Sql, @"@\w+").Select(m => m.Value).Distinct().Order();
            Assert.Equal(["@asOf", "@portfolios"], parameters);
            Assert.Equal(spec.Columns.Length, spec.Formats.Length);
            if (spec.Pivot is { } pivot) Assert.Equal(spec.Columns.Skip(1), pivot);
        }
    }

    [Fact]
    public void Each_source_reads_through_its_own_connection_setting()
    {
        Assert.Equal(ConnectionStrings.Core, InsightCatalog.ConnectionName("core"));
        Assert.Equal(ConnectionStrings.Market, InsightCatalog.ConnectionName("market"));
        Assert.Equal(ConnectionStrings.Surveillance, InsightCatalog.ConnectionName("surveillance"));
        Assert.Equal(ConnectionStrings.Pricing, InsightCatalog.ConnectionName("pricing"));
        Assert.Equal(ConnectionStrings.Reference, InsightCatalog.ConnectionName("reference"));
        Assert.Throws<ArgumentOutOfRangeException>(() => InsightCatalog.ConnectionName("app"));
        Assert.Null(InsightCatalog.Find("core", "curve_moves")); // a grid belongs to one source
        Assert.NotNull(InsightCatalog.Find("market", "curve_moves"));
    }

    [Fact]
    public void Cross_tab_keeps_row_order_fixes_columns_and_appends_unknown_ones()
    {
        object?[][] rows = [["CLO", "AA", 2m], ["CLO", "AAA", 1m], ["RMBS", "n/a", 5m], ["ABS", "AAA", null]];
        var (columns, cells) = InsightPivot.CrossTab("Sector", ["AAA", "AA", "A"], rows);
        Assert.Equal(["Sector", "AAA", "AA", "A", "n/a"], columns);
        Assert.Equal(["CLO", "RMBS", "ABS"], cells.Select(r => r[0]));
        Assert.Equal(["CLO", 1m, 2m, null, null], cells[0]);
        Assert.Equal(["RMBS", null, null, null, 5m], cells[1]);
        Assert.All(cells, r => Assert.Equal(columns.Length, r.Length));
    }

    [Fact]
    public void Cross_tab_of_nothing_is_the_headers_and_no_rows()
    {
        var (columns, cells) = InsightPivot.CrossTab("Sector", ["AAA"], []);
        Assert.Equal(["Sector", "AAA"], columns);
        Assert.Empty(cells);
    }

    [Fact]
    public void Null_keys_read_as_n_a()
    {
        var (columns, cells) = InsightPivot.CrossTab("Sector", ["AAA"], [[null, null, 1m]]);
        Assert.Equal(["Sector", "AAA", "n/a"], columns);
        Assert.Equal(["n/a", null, 1m], cells[0]);
    }

    [Fact]
    public void A_row_that_does_not_line_up_violates_the_invariant()
    {
        var bad = new InsightGrid("g", "G", ["A", "B"], [["x", 1m], ["y"]], []);
        var e = Assert.Throws<InvalidOperationException>(() => InsightPivot.Validate(bad));
        Assert.Contains("invariant", e.Message);
        InsightPivot.Validate(bad with { Rows = [["x", 1m]] });
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_doubles_become_null_never_NaN(double d)
    {
        Assert.Null(InsightPivot.Cell(d));
        Assert.Null(InsightPivot.Cell((float)d));
    }

    [Fact]
    public void Other_cells_pass_through_and_DBNull_is_null()
    {
        Assert.Null(InsightPivot.Cell(DBNull.Value));
        Assert.Null(InsightPivot.Cell(null));
        Assert.Equal(1.5m, InsightPivot.Cell(1.5m));
        Assert.Equal(2.5, InsightPivot.Cell(2.5));
        Assert.Equal(2.5, InsightPivot.Cell(2.5f));
        Assert.Equal(7L, InsightPivot.Cell(7L));
        Assert.Equal("CLO", InsightPivot.Cell("CLO"));
    }

    [Fact]
    public void Shape_pivots_cross_tabs_and_formats_every_column()
    {
        var spec = InsightCatalog.Find("core", "mv_sector_rating")!;
        var grid = InsightsRepository.Shape(spec, [["CLO", "AAA", 10m], ["CLO", "≤B/NR", 2m]]);
        Assert.Equal(spec.Columns, grid.Columns);
        Assert.Equal("text", grid.Format["Sector"]);
        Assert.All(grid.Columns.Skip(1), c => Assert.Equal("money0", grid.Format[c]));
        Assert.Equal(["CLO", 10m, null, null, null, null, 2m], grid.Rows.Single());

        var table = InsightCatalog.Find("core", "top_issuers_mv")!;
        var t = InsightsRepository.Shape(table, [["Issuer 01", 5m, 0.5m, 3L]]);
        Assert.Equal(["Issuer", "MV", "% of book", "Positions"], t.Columns);
        Assert.Equal(["text", "money0", "pct1", "int"], t.Columns.Select(c => t.Format[c]));
    }

    [Fact]
    public void Shape_refuses_a_table_row_with_the_wrong_width()
    {
        var table = InsightCatalog.Find("core", "top_issuers_mv")!;
        Assert.Throws<InvalidOperationException>(() => InsightsRepository.Shape(table, [["Issuer 01", 5m]]));
    }

    [Theory]
    [InlineData(null, null, 4, 10)]
    [InlineData("8", "30", 8, 30)]
    [InlineData("0", "0", 4, 10)]
    [InlineData("17", "101", 4, 10)]
    [InlineData("x", "-1", 4, 10)]
    public void Fan_out_limits_come_from_configuration_within_bounds(string? perRequest, string? global, int expectedPer, int expectedGlobal)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["INSIGHTS_PARALLELISM"] = perRequest,
            ["INSIGHTS_MAX_CONNECTIONS"] = global,
        }).Build();
        Assert.Equal(new InsightsOptions(expectedPer, expectedGlobal), InsightsOptions.From(config));
    }

    [Fact]
    public async Task An_empty_scope_reads_nothing_and_returns_every_grid_empty()
    {
        var registry = new ThrowingRegistry();
        using var repo = new InsightsRepository(registry, new InsightsOptions(4, 10));
        Assert.Same(repo.Options, repo.Options);
        var grids = await repo.ReadAsync(InsightCatalog.For("surveillance"), new DateOnly(2026, 10, 6), [], TestContext.Current.CancellationToken);
        Assert.Equal(InsightCatalog.For("surveillance").Select(s => s.Id), grids.Select(g => g.Id));
        Assert.All(grids, g => Assert.Empty(g.Rows));
        Assert.All(grids, g => Assert.NotEmpty(g.Columns));
        Assert.Equal(0, registry.Opens);
    }

    private sealed class ThrowingRegistry : IDataSourceRegistry
    {
        public int Opens;
        public NpgsqlDataSource Get(string source) => throw new InvalidOperationException("no database in this test");
        public ValueTask<NpgsqlConnection> OpenAsync(string source, CancellationToken ct)
        {
            Opens++;
            throw new InvalidOperationException("no database in this test");
        }
    }
}
