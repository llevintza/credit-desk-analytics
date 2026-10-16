// #181: the comparison half of LastBlockCheck, kept free of HTTP and SQL so Desk.Data.Tests can cover each pass/fail
// path. Program.cs does the I/O, then calls Compare and Render; the printed table and the exit code are unchanged.
using System.Text.Json;
using Desk.Data.Catalog;

namespace LastBlockCheck;

/// <summary>What the API served: the first block's row count, as-of and summary, the last full block, and the aligned last block.</summary>
/// <param name="Last">The last full block's response; its <c>columns</c> and <c>data</c> are read.</param>
public sealed record ApiBlocks(
    int RowCount, DateOnly AsOf, JsonElement Summary,
    int LastStart, IReadOnlyList<long> LastIds, JsonElement Last,
    int AlignedStart, IReadOnlyList<long> AlignedIds);

/// <summary>The independent SQL: the row count, the latest snapshot date, the tail from <c>LastStart</c> with each Sum
/// column's cells, and one SUM per Sum column (each list in the order of the Sum columns).</summary>
public sealed record SqlFacts(
    long Count, DateOnly? Latest,
    IReadOnlyList<long> TailIds, IReadOnlyList<decimal?[]> TailCells,
    IReadOnlyList<decimal?> Sums);

/// <summary>One printed row of the result table.</summary>
public sealed record CheckRow(string Check, string Api, string Sql, bool Ok);

public static class LastBlockComparison
{
    public const int MinRows = 18_000;

    /// <summary>Compares the API against the SQL, one row per check in print order. A NULL, missing or non-number value
    /// is a FAIL row, never an exception.</summary>
    /// <param name="sumColumns">The Risk preset's Sum column names, in the order of <see cref="SqlFacts.TailCells"/> and <see cref="SqlFacts.Sums"/>.</param>
    public static IReadOnlyList<CheckRow> Compare(IReadOnlyList<string> sumColumns, ApiBlocks api, SqlFacts sql)
    {
        var rowCount = api.RowCount;
        var results = new List<CheckRow>
        {
            new("rows ≥ 18,000", $"{rowCount:N0}", $"{sql.Count:N0}", rowCount >= MinRows && rowCount == sql.Count),
            // The checks below reuse the API's as-of; this row makes sure the API's default is the latest snapshot date.
            new("as-of is the latest date", $"{api.AsOf:yyyy-MM-dd}", sql.Latest?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "null", api.AsOf == sql.Latest),
            new($"last block ids (rows {api.LastStart + 1:N0}–{rowCount:N0})", $"{api.LastIds.Count} ids, last {api.LastIds.LastOrDefault()}",
                $"{sql.TailIds.Count} ids, last {sql.TailIds.LastOrDefault()}", api.LastIds.SequenceEqual(sql.TailIds) && api.LastIds.Count == rowCount - api.LastStart),
        };

        // The last block's Sum-column cells, at each column's index in the response's column order. A misaligned column in
        // the columnar serializer at a high offset fails here even when the ids match.
        var apiColumns = api.Last.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
        var data = api.Last.GetProperty("data");
        int cells = 0, cellMismatches = 0, missingColumns = 0;
        for (var j = 0; j < sumColumns.Count; j++)
        {
            var index = apiColumns.IndexOf(sumColumns[j]);
            if (index < 0) { missingColumns++; continue; }
            var apiValues = data[index].EnumerateArray().Select(Number).ToArray();
            for (var row = 0; row < Math.Max(apiValues.Length, sql.TailCells.Count); row++)
            {
                cells++;
                var apiValue = row < apiValues.Length ? apiValues[row] : null;
                var sqlValue = row < sql.TailCells.Count ? sql.TailCells[row][j] : null;
                if (row >= apiValues.Length || row >= sql.TailCells.Count || apiValue != sqlValue) cellMismatches++;
            }
        }
        results.Add(new($"last block Sum-column cells ({sumColumns.Count} columns)", $"{cells - cellMismatches:N0} equal",
            $"{cells:N0} cells, {cellMismatches:N0} differ, {missingColumns} columns missing",
            cells > 0 && cellMismatches == 0 && missingColumns == 0));
        var alignedTail = rowCount - api.AlignedStart;
        results.Add(new($"aligned last block ids (rows {api.AlignedStart + 1:N0}–{rowCount:N0})", $"{api.AlignedIds.Count} ids, last {api.AlignedIds.LastOrDefault()}",
            $"{sql.TailIds.TakeLast(alignedTail).Count()} ids, last {sql.TailIds.LastOrDefault()}",
            api.AlignedIds.Count == alignedTail && api.AlignedIds.SequenceEqual(sql.TailIds.TakeLast(api.AlignedIds.Count))));

        // Every SUM in the summary against an independent SUM.
        for (var j = 0; j < sumColumns.Count; j++)
        {
            // A missing key or a JSON null is a FAIL row, not an exception.
            var apiSum = api.Summary.TryGetProperty(sumColumns[j], out var e) ? Number(e) : null;
            var sqlSum = sql.Sums[j];
            results.Add(new($"SUM({sumColumns[j]})", Show(apiSum), Show(sqlSum), apiSum is not null && apiSum == sqlSum));
        }
        // The loop above must not pass by comparing nothing: the Sum columns the API served equal the ones compared.
        var expected = sumColumns.ToHashSet();
        var served = api.Summary.EnumerateObject().Select(p => p.Name)
            .Where(n => ColumnCatalog.PositionSnapshot.Any(c => c.Name == n && c.Aggregation == Aggregation.Sum)).ToHashSet();
        results.Add(new("SUM columns compared", $"{served.Count}", $"{expected.Count}", expected.Count > 0 && expected.SetEquals(served)));
        return results;
    }

    /// <summary>The markdown the tool prints: a heading, a blank line, then the table.</summary>
    public static IEnumerable<string> Render(int rowCount, DateOnly asOf, int portfolios, IEnumerable<CheckRow> results)
    {
        yield return $"### Last block and summary at {rowCount:N0} rows (as of {asOf:yyyy-MM-dd}, {portfolios} entitled portfolios, Risk preset, market value desc)";
        yield return "";
        yield return "| Check | API | Independent SQL | Result |";
        yield return "|---|---:|---:|---|";
        foreach (var (check, api, sql, ok) in results)
            yield return $"| {check} | {api} | {sql} | {(ok ? "ok" : "FAIL")} |";
    }

    /// <summary>0 when every check passed, 1 otherwise. Compare always returns its fixed rows, so the set is never empty.</summary>
    public static int ExitCode(IReadOnlyCollection<CheckRow> results) => results.All(r => r.Ok) ? 0 : 1;

    static string Show(decimal? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
    static decimal? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : null;
}
