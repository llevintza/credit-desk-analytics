using Desk.Data.Catalog;
using Desk.Data.Grid;
using Npgsql;

namespace Desk.Data.Tests;

/// <summary>
/// #241: the summary's weight is a stored generated column (<see cref="GridSqlBuilder.WeightF8Column"/>), so the
/// summary is a plain aggregate over the snapshot that Postgres can split across parallel workers.
/// </summary>
public sealed class WeightColumnTests(MigratedDatabase db) : IClassFixture<MigratedDatabase>
{
    private static readonly DateOnly AsOf = new(2026, 10, 7);

    [Fact]
    public async Task The_stored_weight_is_the_size_of_the_position_and_follows_every_write()
    {
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(conn, tx,
            "INSERT INTO core.position_snapshot (as_of_date, position_id, portfolio_id, fund_id, bond_id, deal_id, cusip, deal_name, sector, market_value) " +
            "VALUES (@d, 1, 1, 1, 1, 1, 'TEST00001', 'Test deal', 'RMBS', 100.25), (@d, 2, 1, 1, 1, 1, 'TEST00002', 'Test deal', 'RMBS', -300), " +
            "(@d, 3, 1, 1, 1, 1, 'TEST00003', 'Test deal', 'RMBS', 0), (@d, 4, 1, 1, 1, 1, 'TEST00004', 'Test deal', 'RMBS', NULL)");
        Assert.Equal([100.25, 300.0, 0.0, null], await WeightsAsync(conn, tx));

        // An update recomputes it, so the weight can't go stale against market_value.
        await ExecuteAsync(conn, tx, "UPDATE core.position_snapshot SET market_value = -market_value WHERE as_of_date = @d AND position_id = 1");
        Assert.Equal([100.25, 300.0, 0.0, null], await WeightsAsync(conn, tx));
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_stored_weight_cannot_be_written_directly()
    {
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn, tx,
            "INSERT INTO core.position_snapshot (as_of_date, position_id, portfolio_id, fund_id, bond_id, deal_id, cusip, deal_name, sector, market_value, weight_f8) " +
            "VALUES (@d, 5, 1, 1, 1, 1, 'TEST00005', 'Test deal', 'RMBS', 10, 99)"));
        Assert.Equal("428C9", ex.SqlState); // generated_always: only DEFAULT may be written
    }

    [Fact]
    public async Task The_All_preset_summary_can_aggregate_in_parallel_workers()
    {
        // The plan shape, not a timing: given a book worth splitting and free parallel start-up, the summary must be a
        // partial aggregate under a Gather. The old LATERAL (... OFFSET 0) fence kept every aggregate above the Gather,
        // in the leader alone, whatever the costs. The rows and their statistics roll back with the transaction.
        var query = new GridQueryNormalizer(ColumnCatalog.PositionSnapshot)
            .Normalize(new GridRequest(Columns: [.. BuiltInPresets.All]), AsOf, [1, 2, 3]);
        var sql = GridSqlBuilder.Build(query, ColumnCatalog.PositionSnapshot);
        var summary = sql.Sql.Split(";\n")[1];

        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(conn, tx,
            "INSERT INTO core.position_snapshot (as_of_date, position_id, portfolio_id, fund_id, bond_id, deal_id, cusip, deal_name, sector, market_value, spread_bp) " +
            "SELECT @d, 100 + i, 1 + i % 3, 1, 1, 1, 'TEST00001', 'Test deal', 'RMBS', i, i FROM generate_series(1, 20000) AS i; " +
            "ANALYZE core.position_snapshot; SET LOCAL parallel_setup_cost = 0; SET LOCAL min_parallel_table_scan_size = 0");
        await using var cmd = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + summary, conn, tx);
        foreach (var (name, value) in sql.Parameters)
            cmd.Parameters.AddWithValue(name, value);
        var plan = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            while (await r.ReadAsync(TestContext.Current.CancellationToken))
                plan.Add(r.GetString(0).Trim());

        Assert.True(plan[0] == "Finalize Aggregate", string.Join("\n", plan));
        Assert.Contains(plan, line => line.EndsWith("Partial Aggregate", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("Nested Loop", StringComparison.Ordinal));
    }

    private static async Task<double?[]> WeightsAsync(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {GridSqlBuilder.WeightF8Column} FROM core.position_snapshot WHERE as_of_date = @d ORDER BY position_id", conn, tx);
        cmd.Parameters.AddWithValue("d", AsOf);
        var weights = new List<double?>();
        await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await r.ReadAsync(TestContext.Current.CancellationToken))
            weights.Add(r.IsDBNull(0) ? null : r.GetDouble(0));
        return [.. weights];
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("d", AsOf);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
