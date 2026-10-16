using Dapper;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Desk.Data.Marks;
using Npgsql;

namespace Desk.Data.Tests;

/// <summary>
/// README §6 P6: the overlay's totals are the grid's weighted averages with each marked bond's value replaced, over the
/// same filter and portfolio scope; and the universe of marked bonds is scoped to the portfolios asked for.
/// </summary>
public sealed class MarkTotalsSqlTests(MigratedDatabase db) : IClassFixture<MigratedDatabase>
{
    private static readonly DateOnly AsOf = new(2026, 10, 7);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly IReadOnlyList<ColumnDef> Marked = [.. ColumnCatalog.PositionSnapshot.Where(c => c.Name is "price" or "spread_bp")];

    private static GridQuery Query(int[] portfolios, string? sector = null) => new GridQueryNormalizer(ColumnCatalog.PositionSnapshot).Normalize(
        new GridRequest(Columns: ["deal_name", "price", "spread_bp", "market_value"],
            FilterModel: sector is null ? null : new() { ["sector"] = new FilterSpec(FilterType: "set", Values: [sector]) }),
        AsOf, portfolios);

    [Fact]
    public void The_sql_joins_the_marks_as_parameters_and_never_names_a_requested_value()
    {
        var sql = GridSqlBuilder.BuildMarkTotals(Query([1, 2]), ColumnCatalog.PositionSnapshot, Marked, [7], [101.5], [null]);
        Assert.Contains("unnest(@mk_bond::int[], @mk_price::float8[], @mk_spread::float8[])", sql.Sql);
        Assert.Contains("portfolio_id = ANY(@portfolios)", sql.Sql);
        Assert.Contains("COALESCE(m.mk_price, \"price\")", sql.Sql);
        Assert.Contains("COALESCE(m.mk_spread, \"spread_bp\")", sql.Sql);
        Assert.DoesNotContain("101.5", sql.Sql);
        Assert.Equal(new int[] { 7 }, sql.Parameters.Single(p => p.Key == "mk_bond").Value);
        var other = ColumnCatalog.PositionSnapshot.Single(c => c.Name == "yield");
        Assert.Throws<ArgumentException>(() => GridSqlBuilder.BuildMarkTotals(Query([1]), ColumnCatalog.PositionSnapshot, [other], [], [], []));
    }

    [Fact]
    public async Task Totals_replace_marked_bonds_weigh_by_overnight_size_and_keep_the_filter_and_scope()
    {
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(Ct);
        // Bond 10 is held twice in portfolio 1 (one short), bond 11 once; bond 12 in portfolio 2 (out of scope);
        // bond 13 in portfolio 1 but another sector (filtered out).
        await conn.ExecuteAsync(
            "INSERT INTO core.position_snapshot (as_of_date, position_id, portfolio_id, fund_id, bond_id, deal_id, cusip, deal_name, sector, market_value, price, spread_bp) VALUES " +
            "(@d, 1, 1, 1, 10, 1, 'TEST00001', 'Deal A', 'CLO', 100, 98, 200), (@d, 2, 1, 1, 10, 1, 'TEST00001', 'Deal A', 'CLO', -300, 98, 200), " +
            "(@d, 3, 1, 1, 11, 1, 'TEST00002', 'Deal A', 'CLO', 600, 95, NULL), (@d, 4, 2, 1, 12, 1, 'TEST00003', 'Deal A', 'CLO', 1000, 50, 900), " +
            "(@d, 5, 1, 1, 13, 1, 'TEST00004', 'Deal B', 'RMBS', 1000, 60, 800)",
            new { d = AsOf }, tx);

        var marks = new[] { new Mark(10, 5, DateTimeOffset.UnixEpoch, 100, 180), new Mark(12, 5, DateTimeOffset.UnixEpoch, 1, 1) };
        var sql = GridSqlBuilder.BuildMarkTotals(Query([1], "CLO"), ColumnCatalog.PositionSnapshot, Marked,
            [.. marks.Select(m => m.BondId)], [.. marks.Select(m => m.Price)], [.. marks.Select(m => m.SpreadBp)]);
        var row = await QueryRowAsync(conn, tx, sql);

        Assert.Equal(3, row.Count);
        // price: (100·100 + 100·300 + 95·600) / 1000; spread: bond 11 has none, so its weight drops out: 180.
        Assert.Equal((100.0 * 100 + 100 * 300 + 95 * 600) / 1000, row.Price!.Value, 9);
        Assert.Equal(180, row.Spread!.Value, 9);

        // No marks: exactly the grid's own summary for the same view.
        var plain = await QueryRowAsync(conn, tx, GridSqlBuilder.BuildMarkTotals(Query([1], "CLO"), ColumnCatalog.PositionSnapshot, Marked, [], [], []));
        var summarySql = GridSqlBuilder.Build(Query([1], "CLO"), ColumnCatalog.PositionSnapshot);
        await using var cmd = new NpgsqlCommand(summarySql.Sql.Split(";\n")[1], conn, tx);
        foreach (var (name, value) in summarySql.Parameters) cmd.Parameters.AddWithValue(name, value);
        await using (var r = await cmd.ExecuteReaderAsync(Ct))
        {
            await r.ReadAsync(Ct);
            Assert.Equal(r.GetDouble(r.GetOrdinal("price")), plain.Price!.Value, 9);
            Assert.Equal(r.GetDouble(r.GetOrdinal("spread_bp")), plain.Spread!.Value, 9);
        }

        // An empty scope matches nothing: null averages, never NaN.
        var none = await QueryRowAsync(conn, tx, GridSqlBuilder.BuildMarkTotals(Query([]), ColumnCatalog.PositionSnapshot, Marked, [], [], []));
        Assert.Equal(0, none.Count);
        Assert.Null(none.Price);
        await tx.RollbackAsync(Ct);
    }

    [Fact]
    public async Task The_universe_is_the_scoped_bonds_with_their_lowest_positions_values_capped()
    {
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await conn.ExecuteAsync(
            "INSERT INTO core.position_snapshot (as_of_date, position_id, portfolio_id, fund_id, bond_id, deal_id, cusip, deal_name, sector, market_value, price, spread_bp) " +
            "SELECT @d, 100 + i, 1 + i % 2, 1, 1000 + i / 2, 1, 'TEST00001', 'Deal', 'CLO', 10, 90 + i, 100 + i FROM generate_series(0, 39) AS i; " +
            "INSERT INTO core.position_snapshot (as_of_date, position_id, portfolio_id, fund_id, bond_id, deal_id, cusip, deal_name, sector, market_value, price) " +
            "VALUES (@d, 999, 1, 1, 5000, 1, 'TEST00002', 'Deal', 'CLO', 10, NULL)",
            new { d = AsOf }, tx);

        var both = (await conn.QueryAsync<MarkedBond>(MarkRepository.UniverseSql, new { as_of = AsOf, portfolios = new[] { 1, 2 }, max = 100 }, tx)).ToList();
        Assert.Equal(20, both.Count); // bonds 1000..1019; the unpriced bond 5000 is left out
        Assert.Equal(90, both.Single(b => b.BondId == 1000).Price); // position 100 (lower id), not 101
        Assert.Equal(100, both.Single(b => b.BondId == 1000).SpreadBp);

        // Portfolio 2 alone holds the odd positions, which reach every bond too, but with their own values.
        var two = (await conn.QueryAsync<MarkedBond>(MarkRepository.UniverseSql, new { as_of = AsOf, portfolios = new[] { 2 }, max = 100 }, tx)).ToList();
        Assert.Equal(91, two.Single(b => b.BondId == 1000).Price);
        Assert.Empty(await conn.QueryAsync<MarkedBond>(MarkRepository.UniverseSql, new { as_of = AsOf, portfolios = new[] { 3 }, max = 100 }, tx));

        // The cap keeps a stable, hash-ordered subset, not simply the lowest ids.
        var capped = (await conn.QueryAsync<MarkedBond>(MarkRepository.UniverseSql, new { as_of = AsOf, portfolios = new[] { 1, 2 }, max = 5 }, tx)).Select(b => b.BondId).ToList();
        Assert.Equal(5, capped.Count);
        Assert.Equal(capped, (await conn.QueryAsync<MarkedBond>(MarkRepository.UniverseSql, new { as_of = AsOf, portfolios = new[] { 1, 2 }, max = 5 }, tx)).Select(b => b.BondId));
        Assert.NotEqual(Enumerable.Range(1000, 5), capped);
        await tx.RollbackAsync(Ct);
    }

    private sealed record TotalsRow(int Count, double? Price, double? Spread);

    private static async Task<TotalsRow> QueryRowAsync(NpgsqlConnection conn, NpgsqlTransaction tx, GridSql sql)
    {
        await using var cmd = new NpgsqlCommand(sql.Sql, conn, tx);
        foreach (var (name, value) in sql.Parameters) cmd.Parameters.AddWithValue(name, value);
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        await r.ReadAsync(Ct);
        return new TotalsRow(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetDouble(1), r.IsDBNull(2) ? null : r.GetDouble(2));
    }
}
