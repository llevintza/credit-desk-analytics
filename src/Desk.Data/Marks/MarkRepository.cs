using Dapper;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Desk.Data.Sources;

namespace Desk.Data.Marks;

/// <summary>The overlay's totals: the rows the filter matches and each marked column's weighted average.</summary>
public sealed record MarkTotals(int RowCount, IReadOnlyList<SummaryValue> Values);

/// <summary>
/// Reads for the intraday overlay (README §6 P6, ADR-0015). Both are scoped in SQL to the caller's portfolios
/// (<c>portfolio_id = ANY(@portfolios)</c>, ADR-0021); the marks themselves are simulated in memory by
/// <see cref="MarkSimulator"/>.
/// </summary>
public sealed class MarkRepository(IDataSourceRegistry sources)
{
    /// <summary>The columns the overlay replaces with intraday marks.</summary>
    public static readonly IReadOnlyList<string> MarkedColumns = ["price", "spread_bp"];

    /// <summary>
    /// The bonds held in the scope (<c>@portfolios</c>) on <c>@as_of</c>, at most <c>@max</c>, each with its overnight
    /// price and spread (from its lowest position id, so the pick is stable). The bonds are taken in the order of their
    /// first position, every 4th one: the grid's default order is position id, so its first screen always shows some
    /// marked rows among unmarked ones, wherever the scope starts.
    /// </summary>
    public const string UniverseSql = """
        SELECT BondId, Price, SpreadBp
        FROM (
            SELECT bond_id AS BondId, price AS Price, spread_bp AS SpreadBp, row_number() OVER (ORDER BY position_id) AS rn
            FROM (
                SELECT DISTINCT ON (bond_id) bond_id, position_id, price, spread_bp
                FROM core.position_snapshot
                WHERE as_of_date = @as_of AND portfolio_id = ANY(@portfolios) AND price IS NOT NULL
                ORDER BY bond_id, position_id) AS held) AS ranked
        WHERE rn % 4 = 1
        ORDER BY rn
        LIMIT @max
        """;

    public async Task<IReadOnlyList<MarkedBond>> UniverseAsync(DateOnly asOf, int[] portfolios, int max, CancellationToken ct)
    {
        if (portfolios.Length == 0 || max <= 0)
            return [];
        await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        var rows = await conn.QueryAsync<MarkedBond>(new CommandDefinition(UniverseSql,
            new { as_of = asOf, portfolios, max }, cancellationToken: ct));
        return rows.ToArray();
    }

    /// <summary>The marked columns <paramref name="query"/> displays (and so totals): none means there is nothing to overlay.</summary>
    public static ColumnDef[] MarkedIn(GridQuery query) =>
        [.. query.Columns.Where(c => MarkedColumns.Contains(c.Name) && c.Aggregation is Aggregation.WeightedByMarketValue or Aggregation.WeightedByCurrentFace)];

    /// <summary>
    /// The <paramref name="marked"/> columns' totals for <paramref name="query"/>'s filter with <paramref name="marks"/>
    /// applied (<see cref="GridSqlBuilder.BuildMarkTotals"/>).
    /// </summary>
    public async Task<MarkTotals> TotalsAsync(GridQuery query, IReadOnlyCollection<ColumnDef> catalog, IReadOnlyList<ColumnDef> marked,
        IReadOnlyList<Mark> marks, CancellationToken ct)
    {
        var sql = GridSqlBuilder.BuildMarkTotals(query, catalog, marked,
            [.. marks.Select(m => m.BondId)], [.. marks.Select(m => m.Price)], [.. marks.Select(m => m.SpreadBp)]);
        var p = new DynamicParameters();
        foreach (var (name, value) in sql.Parameters)
            p.Add(name, value);
        await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        await using var reader = await conn.ExecuteReaderAsync(new CommandDefinition(sql.Sql, p, cancellationToken: ct));
        await reader.ReadAsync(ct);
        return new MarkTotals(reader.GetInt32(0),
            [.. marked.Select((c, i) => new SummaryValue(c, reader.IsDBNull(i + 1) ? null : reader.GetDouble(i + 1)))]);
    }
}
