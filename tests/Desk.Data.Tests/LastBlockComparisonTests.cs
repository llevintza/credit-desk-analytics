using System.Globalization;
using System.Text.Json;
using LastBlockCheck;

namespace Desk.Data.Tests;

// #181: perf/LastBlockCheck's comparison, without HTTP or SQL. Each check has a passing input and the inputs that must
// fail it: a boundary, a mismatch, a length mismatch, a NULL or missing value, an empty Sum set and malformed JSON.
public sealed class LastBlockComparisonTests
{
    static readonly string[] SumColumns = ["face", "market_value"];
    static readonly DateOnly AsOf = new(2026, 10, 6);

    // 18,001 rows; a 3-row "last full block" from 17,998 and the 1-row aligned block from 18,000.
    const int RowCount = 18_001;
    const int LastStart = 17_998;
    const int AlignedStart = 18_000;

    const string LastJson = """
        {"columns":["position_id","face","market_value"],"data":[[30,20,10],[1.5,2,null],[300,200,100]]}
        """;
    const string SummaryJson = """{"face":10.50,"market_value":600,"factor":0.9}""";

    static ApiBlocks Api(string? last = null, string? summary = null, int rowCount = RowCount,
        long[]? lastIds = null, long[]? alignedIds = null, DateOnly? asOf = null) =>
        new(rowCount, asOf ?? AsOf, Json(summary ?? SummaryJson), LastStart, lastIds ?? [30, 20, 10], Json(last ?? LastJson),
            AlignedStart, alignedIds ?? [10]);

    static SqlFacts Sql(long count = RowCount, DateOnly? latest = null, bool noLatest = false, long[]? ids = null,
        decimal?[][]? cells = null, decimal?[]? sums = null) =>
        new(count, noLatest ? null : latest ?? AsOf, ids ?? [30, 20, 10],
            cells ?? [[1.5m, 300m], [2m, 200m], [null, 100m]], sums ?? [10.5m, 600m]);

    static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // The tool formats N0 in the current culture, so the expected strings pin it.
    static T Invariant<T>(Func<T> f)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return f(); }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    static IReadOnlyList<CheckRow> Compare(ApiBlocks? api = null, SqlFacts? sql = null, string[]? sumColumns = null) =>
        Invariant(() => LastBlockComparison.Compare(sumColumns ?? SumColumns, api ?? Api(), sql ?? Sql()));

    static CheckRow Row(IReadOnlyList<CheckRow> rows, string prefix) => Assert.Single(rows, r => r.Check.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void Matching_api_and_sql_pass_every_check_in_print_order()
    {
        var rows = Compare();

        Assert.Equal(
        [
            new CheckRow("rows ≥ 18,000", "18,001", "18,001", true),
            new CheckRow("as-of is the latest date", "2026-10-06", "2026-10-06", true),
            new CheckRow("last block ids (rows 17,999–18,001)", "3 ids, last 10", "3 ids, last 10", true),
            new CheckRow("last block Sum-column cells (2 columns)", "6 equal", "6 cells, 0 differ, 0 columns missing", true),
            new CheckRow("aligned last block ids (rows 18,001–18,001)", "1 ids, last 10", "1 ids, last 10", true),
            new CheckRow("SUM(face)", "10.50", "10.5", true),
            new CheckRow("SUM(market_value)", "600", "600", true),
            new CheckRow("SUM columns compared", "2", "2", true),
        ], rows);
        Assert.Equal(0, LastBlockComparison.ExitCode(rows));
    }

    [Theory]
    [InlineData(17_999, false)]
    [InlineData(18_000, true)]
    [InlineData(18_001, true)]
    public void Row_count_must_reach_18000(int rowCount, bool ok)
    {
        var row = Row(Compare(Api(rowCount: rowCount), Sql(count: rowCount)), "rows");
        Assert.Equal(ok, row.Ok);
    }

    [Fact]
    public void Row_count_must_equal_the_sql_count()
    {
        var rows = Compare(sql: Sql(count: RowCount - 1));
        Assert.Equal(new CheckRow("rows ≥ 18,000", "18,001", "18,000", false), rows[0]);
        Assert.Equal(1, LastBlockComparison.ExitCode(rows));
    }

    [Fact]
    public void As_of_fails_when_it_is_not_the_latest_date_or_there_is_none()
    {
        Assert.Equal(new CheckRow("as-of is the latest date", "2026-10-06", "2026-10-07", false),
            Row(Compare(sql: Sql(latest: AsOf.AddDays(1))), "as-of"));
        Assert.Equal(new CheckRow("as-of is the latest date", "2026-10-06", "null", false),
            Row(Compare(sql: Sql(noLatest: true)), "as-of"));
    }

    [Fact]
    public void Last_block_ids_fail_on_order_or_length_mismatch()
    {
        Assert.False(Row(Compare(Api(lastIds: [20, 30, 10])), "last block ids").Ok);
        // The API and SQL agree but the block stops short of the last row.
        var shortBlock = Row(Compare(Api(lastIds: [30, 20]), Sql(ids: [30, 20])), "last block ids");
        Assert.Equal(new CheckRow("last block ids (rows 17,999–18,001)", "2 ids, last 20", "2 ids, last 20", false), shortBlock);
        Assert.False(Row(Compare(sql: Sql(ids: [30, 20, 10, 5])), "last block ids").Ok);
    }

    [Fact]
    public void Empty_id_lists_fail_and_show_last_0()
    {
        var row = Row(Compare(Api(lastIds: []), Sql(ids: [])), "last block ids");
        Assert.Equal(new CheckRow("last block ids (rows 17,999–18,001)", "0 ids, last 0", "0 ids, last 0", false), row);
    }

    [Fact]
    public void A_differing_cell_fails_the_cells_check()
    {
        var row = Row(Compare(sql: Sql(cells: [[1.5m, 300m], [2m, 201m], [null, 100m]])), "last block Sum");
        Assert.Equal(new CheckRow("last block Sum-column cells (2 columns)", "5 equal", "6 cells, 1 differ, 0 columns missing", false), row);
    }

    [Theory]
    [InlineData("7", null)]     // a value from the API, NULL in SQL
    [InlineData("null", "7")]   // NULL from the API, a value in SQL
    [InlineData("\"7\"", "7")] // a string from the API is not a number: it reads as null and fails
    public void A_null_or_malformed_cell_against_a_value_fails(string apiFace, string? sqlFace)
    {
        var last = $$"""{"columns":["position_id","face","market_value"],"data":[[30,20,10],[1.5,2,{{apiFace}}],[300,200,100]]}""";
        decimal?[][] cells = [[1.5m, 300m], [2m, 200m], [sqlFace is null ? null : decimal.Parse(sqlFace, CultureInfo.InvariantCulture), 100m]];
        var row = Row(Compare(Api(last: last), Sql(cells: cells)), "last block Sum");
        Assert.Equal(new CheckRow("last block Sum-column cells (2 columns)", "5 equal", "6 cells, 1 differ, 0 columns missing", false), row);
    }

    [Fact]
    public void Null_on_both_sides_is_equal()
    {
        // The default fixture already holds face = null in its third row on both sides.
        Assert.True(Row(Compare(), "last block Sum").Ok);
    }

    [Fact]
    public void A_shorter_api_column_counts_each_missing_row_as_a_mismatch()
    {
        const string last = """{"columns":["position_id","face","market_value"],"data":[[30,20,10],[1.5,2],[300,200,100]]}""";
        var row = Row(Compare(Api(last: last)), "last block Sum");
        Assert.Equal(new CheckRow("last block Sum-column cells (2 columns)", "5 equal", "6 cells, 1 differ, 0 columns missing", false), row);
    }

    [Fact]
    public void Fewer_sql_rows_count_each_missing_row_as_a_mismatch()
    {
        var row = Row(Compare(sql: Sql(cells: [[1.5m, 300m], [2m, 200m]])), "last block Sum");
        Assert.Equal(new CheckRow("last block Sum-column cells (2 columns)", "4 equal", "6 cells, 2 differ, 0 columns missing", false), row);
    }

    [Fact]
    public void A_sum_column_missing_from_the_response_fails()
    {
        const string last = """{"columns":["position_id","market_value"],"data":[[30,20,10],[300,200,100]]}""";
        var row = Row(Compare(Api(last: last)), "last block Sum");
        Assert.Equal(new CheckRow("last block Sum-column cells (2 columns)", "3 equal", "3 cells, 0 differ, 1 columns missing", false), row);
    }

    [Fact]
    public void Columns_are_matched_by_name_not_position()
    {
        const string last = """{"columns":["position_id","market_value","face"],"data":[[30,20,10],[300,200,100],[1.5,2,null]]}""";
        Assert.True(Row(Compare(Api(last: last)), "last block Sum").Ok);
    }

    [Fact]
    public void An_empty_block_compares_no_cells_and_fails()
    {
        const string last = """{"columns":["position_id","face","market_value"],"data":[[],[],[]]}""";
        var row = Row(Compare(Api(last: last), Sql(cells: [])), "last block Sum");
        Assert.Equal(new CheckRow("last block Sum-column cells (2 columns)", "0 equal", "0 cells, 0 differ, 0 columns missing", false), row);
    }

    [Fact]
    public void Aligned_block_fails_on_wrong_length_or_id()
    {
        Assert.Equal(new CheckRow("aligned last block ids (rows 18,001–18,001)", "2 ids, last 10", "1 ids, last 10", false),
            Row(Compare(Api(alignedIds: [20, 10])), "aligned"));
        Assert.False(Row(Compare(Api(alignedIds: [20])), "aligned").Ok);
        Assert.False(Row(Compare(Api(alignedIds: [])), "aligned").Ok);
    }

    [Theory]
    [InlineData("""{"market_value":600}""", "null")]                    // missing key
    [InlineData("""{"face":null,"market_value":600}""", "null")]        // JSON null
    [InlineData("""{"face":"10.5","market_value":600}""", "null")]      // a string, not a number
    [InlineData("""{"face":10.51,"market_value":600}""", "10.51")]      // a different value
    public void A_summary_sum_that_is_missing_null_malformed_or_different_fails(string summary, string shown)
    {
        var rows = Compare(Api(summary: summary));
        Assert.Equal(new CheckRow("SUM(face)", shown, "10.5", false), Row(rows, "SUM(face)"));
        Assert.True(Row(rows, "SUM(market_value)").Ok);
    }

    [Fact]
    public void A_null_sql_sum_fails_even_against_a_value()
    {
        Assert.Equal(new CheckRow("SUM(face)", "10.50", "null", false), Row(Compare(sql: Sql(sums: [null, 600m])), "SUM(face)"));
    }

    [Fact]
    public void A_null_api_sum_fails_even_against_a_null_sql_sum()
    {
        // sum() over all-NULL values is NULL; a missing or null summary must not "match" it.
        var rows = Compare(Api(summary: """{"face":null,"market_value":600}"""), Sql(sums: [null, 600m]));
        Assert.Equal(new CheckRow("SUM(face)", "null", "null", false), Row(rows, "SUM(face)"));
    }

    [Fact]
    public void An_empty_sum_set_fails_the_compared_check()
    {
        var rows = Compare(Api(summary: "{}"), Sql(cells: [[], [], []], sums: []), sumColumns: []);
        Assert.DoesNotContain(rows, r => r.Check.StartsWith("SUM(", StringComparison.Ordinal));
        Assert.Equal(new CheckRow("SUM columns compared", "0", "0", false), Row(rows, "SUM columns"));
        Assert.Equal(new CheckRow("last block Sum-column cells (0 columns)", "0 equal", "0 cells, 0 differ, 0 columns missing", false),
            Row(rows, "last block Sum"));
        Assert.Equal(1, LastBlockComparison.ExitCode(rows));
    }

    [Fact]
    public void A_served_sum_column_that_was_not_compared_fails()
    {
        // current_face is a catalog Sum column the API summed but the check did not compare.
        var row = Row(Compare(Api(summary: """{"face":10.50,"market_value":600,"current_face":1}""")), "SUM columns");
        Assert.Equal(new CheckRow("SUM columns compared", "3", "2", false), row);
    }

    [Fact]
    public void Non_sum_and_unknown_summary_keys_are_ignored()
    {
        // factor is weighted, not a Sum; nonsense is not in the catalog.
        Assert.True(Row(Compare(Api(summary: """{"face":10.50,"market_value":600,"factor":0.9,"nonsense":1}""")), "SUM columns").Ok);
    }

    [Fact]
    public void A_response_without_columns_or_data_throws_as_before()
    {
        Assert.Throws<KeyNotFoundException>(() => Compare(Api(last: """{"data":[]}""")));
        Assert.Throws<KeyNotFoundException>(() => Compare(Api(last: """{"columns":["face"]}""")));
    }

    [Fact]
    public void Render_prints_the_heading_blank_line_and_table()
    {
        var lines = Invariant(() => LastBlockComparison.Render(20_001, AsOf, 4,
            [new CheckRow("rows ≥ 18,000", "20,001", "20,001", true), new CheckRow("SUM(face)", "null", "1", false)]).ToList());
        Assert.Equal(
        [
            "### Last block and summary at 20,001 rows (as of 2026-10-06, 4 entitled portfolios, Risk preset, market value desc)",
            "",
            "| Check | API | Independent SQL | Result |",
            "|---|---:|---:|---|",
            "| rows ≥ 18,000 | 20,001 | 20,001 | ok |",
            "| SUM(face) | null | 1 | FAIL |",
        ], lines);
    }

    [Fact]
    public void Exit_code_is_1_when_any_row_fails()
    {
        Assert.Equal(0, LastBlockComparison.ExitCode([new("a", "", "", true)]));
        Assert.Equal(1, LastBlockComparison.ExitCode([new("a", "", "", true), new("b", "", "", false)]));
    }
}
