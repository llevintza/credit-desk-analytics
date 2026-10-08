using System.Text;
using Desk.Data.Catalog;

namespace Desk.Data.Grid;

/// <summary>The SQL for one grid block: the page and the summary in one batch, plus its parameters.</summary>
public sealed record GridSql(string Sql, IReadOnlyList<KeyValuePair<string, object>> Parameters);

/// <summary>
/// Emits SQL for a whitelisted <see cref="GridQuery"/>. Sorting uses Postgres' default NULL placement (last when
/// ascending, first when descending), which lets one btree index serve both directions (ADR-0008). Identifiers come only from the catalog (and are quoted);
/// every value is a parameter (AGENTS.md SQL safety). The text depends only on the query's shape, never on its
/// values, so Postgres sees a small set of statements.
/// </summary>
public static class GridSqlBuilder
{
    public const string Table = "core.position_snapshot";
    public const string WeightColumn = "market_value";

    /// <summary>
    /// The weight as float8, computed once per row. Writing <c>market_value::float8</c> inside each weighted average
    /// repeats the numeric→float8 cast per aggregate per row: for the "All" preset (174 aggregates) that was 2.4 s of
    /// a 2.43 s summary. The <c>OFFSET 0</c> keeps Postgres from flattening the LATERAL back into the expressions;
    /// with it the same summary takes 96 ms (ADR-0006).
    /// </summary>
    internal const string SummaryFrom = Table + " CROSS JOIN LATERAL (SELECT " + WeightExpression + " AS weight_f8 OFFSET 0) w";

    /// <summary>
    /// Weighted averages weigh by the size of a position, <c>ABS(market_value)</c> (#131, README §8). A signed weight is
    /// right only while every position is long: a short (or any negative value) would cancel longs in the denominator
    /// and could flip its sign, giving a meaningless average instead of <c>null</c>. Zero weights still count for
    /// nothing, and no weight at all gives <c>null</c>.
    /// </summary>
    internal const string WeightExpression = "abs(" + WeightColumn + ")::float8";

    /// <param name="includeSummary">
    /// False when the caller already holds this filter's totals (they don't depend on paging or sort): the batch is
    /// then the indexed page read alone, without the full-filter aggregate scan.
    /// </param>
    public static GridSql Build(GridQuery q, IEnumerable<ColumnDef> catalog, bool includeSummary = true)
    {
        var p = new List<KeyValuePair<string, object>>
        {
            new("as_of", q.AsOf),
            new("portfolios", q.PortfolioIds),
            new("offset", q.Offset),
            new("limit", q.Limit),
        };
        var where = Where(q, catalog, p);

        var sql = new StringBuilder();
        sql.Append("SELECT ").AppendJoin(", ", q.Columns.Select(c => Quote(c.Name)))
           .Append(" FROM ").Append(Table).Append(" WHERE ").Append(where)
           .Append(" ORDER BY ").AppendJoin(", ", q.Sort.Select(s => $"{Quote(s.Column.Name)} {(s.Descending ? "DESC" : "ASC")}"))
           .Append(" OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;");
        if (!includeSummary)
            return new GridSql(sql.ToString(), p);

        // Totals over ALL filtered rows (the pinned summary row), in the same round trip.
        sql.Append('\n').Append("SELECT COUNT(*)::int AS row_count");
        foreach (var c in q.Columns.Where(c => c.Aggregation != Aggregation.None))
            sql.Append(", ").Append(Aggregate(c)).Append(" AS ").Append(Quote(c.Name));
        sql.Append(" FROM ").Append(SummaryFrom).Append(" WHERE ").Append(where).Append(';');

        return new GridSql(sql.ToString(), p);
    }

    /// <summary>The page query alone, for CSV export (no OFFSET window beyond the cap, no summary).</summary>
    public static GridSql BuildExport(GridQuery q, IEnumerable<ColumnDef> catalog)
    {
        var p = new List<KeyValuePair<string, object>>
        {
            new("as_of", q.AsOf),
            new("portfolios", q.PortfolioIds),
            new("limit", q.Limit),
        };
        var where = Where(q, catalog, p);
        var sql = new StringBuilder();
        sql.Append("SELECT ").AppendJoin(", ", q.Columns.Select(c => Quote(c.Name)))
           .Append(" FROM ").Append(Table).Append(" WHERE ").Append(where)
           .Append(" ORDER BY ").AppendJoin(", ", q.Sort.Select(s => $"{Quote(s.Column.Name)} {(s.Descending ? "DESC" : "ASC")}"))
           .Append(" LIMIT @limit;");
        return new GridSql(sql.ToString(), p);
    }

    /// <summary>
    /// SUM for additive measures; market-value-weighted average otherwise. Rows where the measure is NULL don't
    /// count toward the weight, and zero or no weight gives NULL, never NaN (README §8).
    /// </summary>
    internal static string Aggregate(ColumnDef c) => c.Aggregation switch
    {
        Aggregation.Sum => $"SUM({Quote(c.Name)})",
        _ => $"SUM({Quote(c.Name)} * w.weight_f8) / NULLIF(SUM(w.weight_f8) FILTER (WHERE {Quote(c.Name)} IS NOT NULL), 0)",
    };

    private static string Where(GridQuery q, IEnumerable<ColumnDef> catalog, List<KeyValuePair<string, object>> p)
    {
        var clauses = new List<string> { "as_of_date = @as_of", "portfolio_id = ANY(@portfolios)" };
        foreach (var f in q.Filters)
        {
            var parts = f.Conditions.Select(c => Condition(f.Column, c, p)).ToList();
            clauses.Add(parts.Count == 1 ? parts[0] : $"({string.Join(f.Or ? " OR " : " AND ", parts)})");
        }

        if (q.QuickTokens.Count > 0)
        {
            // Quick filter: every token must appear in one of the text columns (like AG Grid's client-side quick filter).
            var haystack = $"concat_ws(' ', {string.Join(", ", catalog.Where(c => c.Kind == ColumnKind.Text).Select(c => Quote(c.Name)))})";
            foreach (var token in q.QuickTokens)
                clauses.Add($"{haystack} ILIKE {Add(p, $"%{EscapeLike(token)}%")}");
        }
        return string.Join(" AND ", clauses);
    }

    private static string Condition(ColumnDef col, GridCondition c, List<KeyValuePair<string, object>> p)
    {
        var name = Quote(col.Name);
        return (c.Kind, c.Op) switch
        {
            (_, FilterOp.Blank) => $"{name} IS NULL",
            (_, FilterOp.NotBlank) => $"{name} IS NOT NULL",
            (FilterKind.Set, _) => SetCondition(col, c.Values!, p),

            (FilterKind.Text, FilterOp.Contains) => $"{name} ILIKE {Add(p, $"%{EscapeLike((string)c.Value!)}%")}",
            (FilterKind.Text, FilterOp.NotContains) => $"({name} IS NULL OR {name} NOT ILIKE {Add(p, $"%{EscapeLike((string)c.Value!)}%")})",
            (FilterKind.Text, FilterOp.StartsWith) => $"{name} ILIKE {Add(p, $"{EscapeLike((string)c.Value!)}%")}",
            (FilterKind.Text, FilterOp.EndsWith) => $"{name} ILIKE {Add(p, $"%{EscapeLike((string)c.Value!)}")}",
            (FilterKind.Text, FilterOp.Equals) => $"{name} ILIKE {Add(p, EscapeLike((string)c.Value!))}",
            (FilterKind.Text, _) => $"({name} IS NULL OR {name} NOT ILIKE {Add(p, EscapeLike((string)c.Value!))})",

            // Number and date filters share comparison operators; AG Grid's inRange is exclusive at both ends.
            (_, FilterOp.Equals) => $"{name} = {Add(p, c.Value!)}",
            (_, FilterOp.NotEqual) => $"({name} IS NULL OR {name} <> {Add(p, c.Value!)})",
            (_, FilterOp.LessThan) => $"{name} < {Add(p, c.Value!)}",
            (_, FilterOp.LessThanOrEqual) => $"{name} <= {Add(p, c.Value!)}",
            (_, FilterOp.GreaterThan) => $"{name} > {Add(p, c.Value!)}",
            (_, FilterOp.GreaterThanOrEqual) => $"{name} >= {Add(p, c.Value!)}",
            _ => $"({name} > {Add(p, c.Value!)} AND {name} < {Add(p, c.ValueTo!)})",
        };
    }

    private static string SetCondition(ColumnDef col, IReadOnlyList<string?> values, List<KeyValuePair<string, object>> p)
    {
        var name = Quote(col.Name);
        var nonNull = values.OfType<string>().ToArray();
        var includeBlanks = nonNull.Length < values.Count;
        // Non-text columns compare on their text form, which is what AG Grid's set filter lists.
        var target = col.Kind == ColumnKind.Text ? name : $"{name}::text";
        var parts = new List<string>();
        if (nonNull.Length > 0) parts.Add($"{target} = ANY({Add(p, nonNull)})");
        if (includeBlanks) parts.Add($"{name} IS NULL");
        return parts.Count switch
        {
            0 => "FALSE", // nothing selected: AG Grid shows no rows
            1 => parts[0],
            _ => $"({parts[0]} OR {parts[1]})",
        };
    }

    private static string Add(List<KeyValuePair<string, object>> p, object value)
    {
        var name = $"p{p.Count}";
        p.Add(new(name, value));
        return "@" + name;
    }

    /// <summary>Catalog names are snake_case already; quoting keeps reserved words (e.g. <c>class</c>) safe.</summary>
    internal static string Quote(string identifier) => $"\"{identifier}\"";

    /// <summary>LIKE wildcards in user text match literally (Postgres' default LIKE escape is backslash).</summary>
    internal static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
