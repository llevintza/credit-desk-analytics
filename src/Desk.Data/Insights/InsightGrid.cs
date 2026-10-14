namespace Desk.Data.Insights;

/// <summary>
/// One small P3 grid (README §6 P3): a header per column, rows of cells (the first is the row label) and a display
/// format per column. Cells are <c>string</c>, <c>decimal</c> (money, ratios from numeric), <c>double</c> or
/// <c>long</c>/<c>int</c>, or null; never NaN.
/// </summary>
public sealed record InsightGrid(string Id, string Title, string[] Columns, object?[][] Rows, Dictionary<string, string> Format);

/// <summary>One source's grids for an as-of date and portfolio scope.</summary>
public sealed record InsightsResult(string Source, DateOnly AsOf, InsightGrid[] Grids);

/// <summary>
/// Long rows (row key, column key, value) → a cross-tab, at the edge as P2 does (ADR-0011): row keys keep the SQL's
/// order, columns follow the fixed list and any key outside it is appended in first-seen order (e.g. "n/a").
/// </summary>
public static class InsightPivot
{
    public static (string[] Columns, object?[][] Rows) CrossTab(string rowHeader, IReadOnlyList<string> columnOrder, IReadOnlyList<object?[]> longRows)
    {
        var columns = columnOrder.ToList();
        var rowKeys = new List<string>();
        var cells = new Dictionary<(string Row, string Col), object?>();
        foreach (var r in longRows)
        {
            var row = r[0]?.ToString() ?? "n/a";
            var col = r[1]?.ToString() ?? "n/a";
            if (!cells.ContainsKey((row, col)) && !rowKeys.Contains(row)) rowKeys.Add(row);
            if (!columns.Contains(col)) columns.Add(col);
            cells[(row, col)] = r[2];
        }
        var rows = rowKeys
            .Select(rk => (object?[])[rk, .. columns.Select(ck => cells.GetValueOrDefault((rk, ck)))])
            .ToArray();
        return ([rowHeader, .. columns], rows);
    }

    /// <summary>Every row has exactly one cell per column; a mismatch is a bug (the API returns a logged 500).</summary>
    public static void Validate(InsightGrid grid)
    {
        foreach (var row in grid.Rows)
            if (row.Length != grid.Columns.Length)
                throw new InvalidOperationException(
                    $"Insight grid '{grid.Id}' invariant violated: a row has {row.Length} cells for {grid.Columns.Length} columns.");
    }

    /// <summary>Database values → JSON-safe cells: DBNull and non-finite doubles become null (never NaN).</summary>
    public static object? Cell(object? value) => value switch
    {
        null or DBNull => null,
        double d => double.IsFinite(d) ? d : null,
        float f => float.IsFinite(f) ? (double)f : null,
        _ => value,
    };
}
