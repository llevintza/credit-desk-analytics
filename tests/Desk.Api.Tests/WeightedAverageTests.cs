using Desk.Data.Catalog;
using Desk.Data.Grid;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>
/// #131 / README §8 (and §6 P1): the summary's weighted average, evaluated by Postgres exactly as <see cref="GridSqlBuilder"/>
/// emits it, over hand-made rows with mixed-sign, zero and NULL weights. #459: prices weigh by |current face| instead.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WeightedAverageTests(PostgresApiFactory api)
{
    private static readonly ColumnDef Spread = ColumnCatalog.PositionSnapshot.Single(c => c.Name == "spread_bp");

    /// <summary>
    /// The builder's aggregate over (spread_bp, market_value) rows given as a VALUES list, with the weight computed by the
    /// same expression as the snapshot's stored weight column (MigrationTests pins the two together).
    /// </summary>
    private async Task<double?> WeightedAsync(string rows)
    {
        Assert.Equal(Aggregation.WeightedByMarketValue, Spread.Aggregation);
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {GridSqlBuilder.Aggregate(Spread)} FROM (SELECT spread_bp, {GridSqlBuilder.WeightExpression} AS {GridSqlBuilder.WeightF8Column} " +
            $"FROM (VALUES {rows}) AS v(spread_bp, market_value)) AS t", conn);
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken) is double d ? d : null;
    }

    [Fact]
    public async Task A_short_position_counts_by_its_size_not_against_the_longs()
    {
        // Signed weights would give (100·1000 − 300·1000) / (1000 − 1000): a zero denominator, so NULL for a real book.
        Assert.Equal(200, (await WeightedAsync("(100.0::float8, 1000.0::numeric), (300.0, -1000.0)"))!.Value, 9);
        // And a mostly-short book can't flip the sign of the average: (100·100 + 300·900) / 1000, not / −800.
        Assert.Equal(280, (await WeightedAsync("(100.0::float8, 100.0::numeric), (300.0, -900.0)"))!.Value, 9);
    }

    [Fact]
    public async Task Zero_and_null_weights_count_for_nothing_and_no_weight_is_null_never_nan()
    {
        // A zero weight and a NULL measure both drop out: (100·500) / 500.
        Assert.Equal(100, (await WeightedAsync("(100.0::float8, 500.0::numeric), (900.0, 0.0), (NULL, 700.0)"))!.Value, 9);
        Assert.Null(await WeightedAsync("(100.0::float8, 0.0::numeric), (900.0, 0.0)"));
        Assert.Null(await WeightedAsync("(NULL::float8, 500.0::numeric)"));
        // A NULL weight (market_value is nullable) excludes the row from both sides: (100·500) / 500.
        Assert.Equal(100, (await WeightedAsync("(100.0::float8, 500.0::numeric), (900.0, NULL)"))!.Value, 9);
        Assert.Null(await WeightedAsync("(100.0::float8, NULL::numeric)"));
    }

    private static readonly ColumnDef Price = ColumnCatalog.PositionSnapshot.Single(c => c.Name == "price");

    /// <summary>The builder's price aggregate over (price, current_face, market_value) rows, as <see cref="WeightedAsync"/>.</summary>
    private async Task<double?> FaceWeightedAsync(string rows)
    {
        Assert.Equal(Aggregation.WeightedByCurrentFace, Price.Aggregation);
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {GridSqlBuilder.Aggregate(Price)} FROM (SELECT price, {GridSqlBuilder.FaceWeightExpression} AS {GridSqlBuilder.FaceWeightF8Column}, " +
            $"{GridSqlBuilder.WeightExpression} AS {GridSqlBuilder.WeightF8Column} " +
            $"FROM (VALUES {rows}) AS v(price, current_face, market_value)) AS t", conn);
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken) is double d ? d : null;
    }

    [Fact]
    public async Task Prices_weigh_by_face_not_by_market_value()
    {
        // Par-weighted: (80·1000 + 100·3000) / 4000 = 95. By |MV| (800 and 3000) it would be Σp²f / Σpf ≈ 95.79.
        Assert.Equal(95, (await FaceWeightedAsync("(80.0::float8, 1000.0::numeric, 800.0::numeric), (100.0, 3000.0, 3000.0)"))!.Value, 9);
        // A row with face but no market value still counts: the price weight doesn't read market_value at all.
        Assert.Equal(90, (await FaceWeightedAsync("(80.0::float8, 1000.0::numeric, NULL::numeric), (100.0, 1000.0, 0.0)"))!.Value, 9);
    }

    [Fact]
    public async Task A_short_holding_counts_by_its_face_not_against_the_longs()
    {
        // Signed face would give (90·1000 − 110·1000) / 0: NULL for a real book.
        Assert.Equal(100, (await FaceWeightedAsync("(90.0::float8, 1000.0::numeric, 900.0::numeric), (110.0, -1000.0, -1100.0)"))!.Value, 9);
        // And a mostly-short book can't flip it: (90·100 + 110·900) / 1000 = 108, not / −800.
        Assert.Equal(108, (await FaceWeightedAsync("(90.0::float8, 100.0::numeric, 90.0::numeric), (110.0, -900.0, -990.0)"))!.Value, 9);
    }

    [Fact]
    public async Task Zero_and_null_faces_count_for_nothing_and_no_face_is_null_never_nan()
    {
        // A zero face and a NULL price both drop out: (95·500) / 500.
        Assert.Equal(95, (await FaceWeightedAsync("(95.0::float8, 500.0::numeric, 475.0::numeric), (60.0, 0.0, 0.0), (NULL, 700.0, 650.0)"))!.Value, 9);
        Assert.Null(await FaceWeightedAsync("(95.0::float8, 0.0::numeric, 100.0::numeric), (60.0, 0.0, 100.0)"));
        Assert.Null(await FaceWeightedAsync("(NULL::float8, 500.0::numeric, 475.0::numeric)"));
        // A NULL face (current_face is nullable) excludes the row from both sides: (95·500) / 500.
        Assert.Equal(95, (await FaceWeightedAsync("(95.0::float8, 500.0::numeric, 475.0::numeric), (60.0, NULL, 600.0)"))!.Value, 9);
        Assert.Null(await FaceWeightedAsync("(95.0::float8, NULL::numeric, 475.0::numeric)"));
    }
}
