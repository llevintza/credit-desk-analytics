using System.Data;
using Desk.Data;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Desk.Data.Sources;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>#241: <see cref="GridRepository.ReadBlockAsync"/> with <c>concurrentSummary</c>: the page and the totals on two connections at once.</summary>
[Collection(ApiCollection.Name)]
public sealed class ConcurrentSummaryTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyCollection<ColumnDef> Catalog = ColumnCatalog.PositionSnapshot;
    private static readonly ColumnDef PositionId = Catalog.Single(c => c.Name == GridQueryNormalizer.RowIdColumn);

    private static GridQuery AllPreset(params int[] portfolios) => new GridQueryNormalizer(Catalog).Normalize(
        new GridRequest(Columns: [.. BuiltInPresets.All], SortModel: [new("market_value", "desc")]), PostgresApiFactory.AsOf, portfolios);

    private (GridRepository Grid, CountingSources Sources) Repository()
    {
        var sources = new CountingSources(api.Services.GetRequiredService<IDataSourceRegistry>());
        return (new GridRepository(sources), sources);
    }

    [Fact]
    public async Task A_concurrent_first_view_reads_the_same_block_and_totals_as_one_batch_on_two_connections_at_once()
    {
        var (grid, sources) = Repository();
        var query = AllPreset(1, 2);

        var batch = await grid.ReadBlockAsync(query, Catalog, Ct);
        Assert.Equal(1, sources.Peak);
        sources.Reset();
        var concurrent = await grid.ReadBlockAsync(query, Catalog, Ct, concurrentSummary: true);

        Assert.Equal(2, sources.Peak);
        Assert.Equal(0, sources.Open);
        Assert.True(batch.RowCount > 0);
        Assert.Equal(batch.RowCount, concurrent.RowCount);
        Assert.Equal(batch.Summary, concurrent.Summary);
        Assert.Equal(batch.Rows, concurrent.Rows);
        Assert.Equal(batch.Columns.Select(Values), concurrent.Columns.Select(Values));
    }

    [Fact]
    public async Task Concurrent_totals_keep_the_portfolio_scope_in_sql()
    {
        var (grid, _) = Repository();
        var one = await grid.ReadBlockAsync(AllPreset(1), Catalog, Ct, concurrentSummary: true);
        var none = await grid.ReadBlockAsync(AllPreset(), Catalog, Ct, concurrentSummary: true);

        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*)::int FROM core.position_snapshot WHERE as_of_date = @d AND portfolio_id = 1", conn);
        cmd.Parameters.AddWithValue("d", PostgresApiFactory.AsOf);
        Assert.Equal((int)(await cmd.ExecuteScalarAsync(Ct))!, one.RowCount);
        Assert.Equal(0, none.RowCount);
        Assert.Equal(0, none.Rows);
    }

    [Fact]
    public async Task Known_totals_read_only_the_page_whatever_the_flag()
    {
        var (grid, sources) = Repository();
        var known = new GridSummary(7, []);
        var block = await grid.ReadBlockAsync(AllPreset(1), Catalog, Ct, known, concurrentSummary: true);
        Assert.Equal(1, sources.Peak);
        Assert.Equal(7, block.RowCount);
    }

    [Fact]
    public async Task A_failing_page_read_cancels_the_totals_and_is_what_the_caller_sees()
    {
        var (grid, sources) = Repository();
        // Sorted by a column the table lacks: only the page statement fails (42703); the totals don't sort.
        var missing = new ColumnDef("no_such_column", "Test", ColumnKind.Ratio, Aggregation.None, "Missing");
        var query = AllPreset(1) with { Sort = [new GridSort(missing, false), new GridSort(PositionId, false)] };

        var e = await Assert.ThrowsAsync<PostgresException>(() => grid.ReadBlockAsync(query, Catalog, Ct, concurrentSummary: true));
        Assert.Equal(PostgresErrorCodes.UndefinedColumn, e.SqlState);
        Assert.Equal(0, sources.Open);
    }

    [Fact]
    public async Task A_failing_summary_is_what_the_caller_sees_not_the_cancelled_page()
    {
        var (grid, sources) = Repository();
        // SUM over a text column: the page reads it fine, the totals statement fails (42883).
        var summedText = new ColumnDef("deal_name", "Test", ColumnKind.Text, Aggregation.Sum, "Deal");
        var query = AllPreset(1) with { Columns = [PositionId, summedText] };

        var e = await Assert.ThrowsAsync<PostgresException>(() => grid.ReadBlockAsync(query, Catalog, Ct, concurrentSummary: true));
        Assert.Equal(PostgresErrorCodes.UndefinedFunction, e.SqlState);
        Assert.Equal(0, sources.Open);
    }

    [Fact]
    public async Task A_cancelled_caller_cancels_both_reads()
    {
        var (grid, sources) = Repository();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grid.ReadBlockAsync(AllPreset(1), Catalog, cancelled.Token, concurrentSummary: true));
        Assert.Equal(0, sources.Open);
    }

    private static IEnumerable<object?> Values(GridColumn column) => column switch
    {
        Int64Column c => c.Values.Cast<object?>(),
        Int32Column c => c.Values.Cast<object?>(),
        DoubleColumn c => c.Values.Cast<object?>(),
        DecimalColumn c => c.Values.Cast<object?>(),
        TextColumn c => c.Values.Cast<object?>(),
        DateColumn c => c.Values.Cast<object?>(),
        BoolColumn c => c.Values.Cast<object?>(),
        _ => throw new InvalidOperationException(column.GetType().Name),
    };

    /// <summary>Counts the <c>core</c> connections open at once (Dapper's reads); everything else passes through.</summary>
    private sealed class CountingSources(IDataSourceRegistry inner) : IDataSourceRegistry
    {
        private int _open;
        private int _peak;

        public int Open => Volatile.Read(ref _open);

        public int Peak => Volatile.Read(ref _peak);

        public void Reset() => Volatile.Write(ref _peak, Open);

        public NpgsqlDataSource Get(string source) => inner.Get(source);

        public async ValueTask<NpgsqlConnection> OpenAsync(string source, CancellationToken ct)
        {
            var conn = await inner.OpenAsync(source, ct);
            if (source != ConnectionStrings.Core)
                return conn;
            var now = Interlocked.Increment(ref _open);
            for (var peak = Peak; now > peak && Interlocked.CompareExchange(ref _peak, now, peak) != peak; peak = Peak) { }
            conn.StateChange += (_, e) =>
            {
                if (e.CurrentState == ConnectionState.Closed) Interlocked.Decrement(ref _open);
            };
            return conn;
        }
    }
}
