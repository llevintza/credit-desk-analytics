using Desk.Data.Catalog;
using Desk.Data.Grid;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>
/// #131 / README §8 (and §6 P1): the summary's weighted average, evaluated by Postgres exactly as <see cref="GridSqlBuilder"/>
/// emits it, over hand-made rows with mixed-sign, zero and NULL weights.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WeightedAverageTests(PostgresApiFactory api)
{
    private static readonly ColumnDef Spread = ColumnCatalog.PositionSnapshot.Single(c => c.Name == "spread_bp");

    /// <summary>The builder's aggregate and weight over (spread_bp, market_value) rows given as a VALUES list.</summary>
    private async Task<double?> WeightedAsync(string rows)
    {
        Assert.Equal(Aggregation.WeightedByMarketValue, Spread.Aggregation);
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {GridSqlBuilder.Aggregate(Spread)} FROM (VALUES {rows}) AS t(spread_bp, market_value) " +
            $"CROSS JOIN LATERAL (SELECT {GridSqlBuilder.WeightExpression} AS weight_f8 OFFSET 0) w", conn);
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
}
