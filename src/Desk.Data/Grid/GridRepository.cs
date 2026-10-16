using System.Data.Common;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Dapper;
using Desk.Data.Catalog;
using Desk.Data.Sources;

namespace Desk.Data.Grid;

/// <summary>
/// Runs grid SQL on the <c>core</c> source with Dapper (ADR-0006): Dapper binds the parameters and owns the command,
/// and the rows are read column by column into typed buffers instead of Dapper's dynamic rows.
/// </summary>
public sealed class GridRepository(IDataSourceRegistry sources)
{
    /// <param name="knownSummary">This view's totals from an earlier block; when given, only the page is read.</param>
    /// <param name="concurrentSummary">
    /// When the totals are read too, read them on a second pooled connection while the page is read on the first
    /// (#241, ADR-0006). The caller must hold a database permit for that second connection; without one the page and
    /// the totals go in one batch on one connection. Ignored when <paramref name="knownSummary"/> is given.
    /// </param>
    public async Task<GridBlock> ReadBlockAsync(GridQuery query, IReadOnlyCollection<ColumnDef> catalog, CancellationToken ct,
        GridSummary? knownSummary = null, bool concurrentSummary = false)
    {
        var started = Stopwatch.GetTimestamp();
        if (knownSummary is null && concurrentSummary)
        {
            var (columns, totals) = await ReadPageAndSummaryAsync(query, catalog, ct);
            return new GridBlock(columns, totals.RowCount, totals.Values, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var sql = GridSqlBuilder.Build(query, catalog, includeSummary: knownSummary is null);
        await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        await using var reader = await conn.ExecuteReaderAsync(Command(sql, ct));
        var page = await ReadPageAsync(reader, query, ct);

        if (knownSummary is not null)
            return new GridBlock(page, knownSummary.RowCount, knownSummary.Values, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        await reader.NextResultAsync(ct);
        var summary = await ReadSummaryAsync(reader, query, ct);
        return new GridBlock(page, summary.RowCount, summary.Values, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    /// The page and the totals at once, each on its own pooled connection (never one connection shared by two
    /// commands). The summary starts first: it is the longer read. When one read fails, the other is cancelled, and
    /// the failure is what the caller sees, not that cancellation.
    /// </summary>
    private async Task<(GridColumn[] Page, GridSummary Summary)> ReadPageAndSummaryAsync(GridQuery query, IReadOnlyCollection<ColumnDef> catalog, CancellationToken ct)
    {
        using var sibling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var summary = CancelSiblingOnFailure(ReadAsync(GridSqlBuilder.BuildSummary(query, catalog), ReadSummaryAsync, query, sibling.Token), sibling);
        var page = CancelSiblingOnFailure(ReadAsync(GridSqlBuilder.Build(query, catalog, includeSummary: false), ReadPageAsync, query, sibling.Token), sibling);
        var both = Task.WhenAll(summary, page);
        try
        {
            await both;
        }
        catch when (both.Exception is { } e && e.InnerExceptions.FirstOrDefault(x => x is not OperationCanceledException) is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
        return (await page, await summary);
    }

    private static async Task<T> CancelSiblingOnFailure<T>(Task<T> read, CancellationTokenSource sibling)
    {
        try
        {
            return await read;
        }
        catch
        {
            await sibling.CancelAsync();
            throw;
        }
    }

    private async Task<T> ReadAsync<T>(GridSql sql, Func<DbDataReader, GridQuery, CancellationToken, Task<T>> read, GridQuery query, CancellationToken ct)
    {
        await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        await using var reader = await conn.ExecuteReaderAsync(Command(sql, ct));
        return await read(reader, query, ct);
    }

    private static async Task<GridColumn[]> ReadPageAsync(DbDataReader reader, GridQuery query, CancellationToken ct)
    {
        var columns = query.Columns.Select(GridColumn.For).ToArray();
        while (await reader.ReadAsync(ct))
            for (var i = 0; i < columns.Length; i++)
                columns[i].Read(reader, i);
        return columns;
    }

    private static async Task<GridSummary> ReadSummaryAsync(DbDataReader reader, GridQuery query, CancellationToken ct)
    {
        await reader.ReadAsync(ct);
        var rowCount = reader.GetInt32(0);
        var values = query.Columns.Where(c => c.Aggregation != Aggregation.None)
            .Select((c, i) => new SummaryValue(c, reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1)))
            .ToArray();
        return new GridSummary(rowCount, values);
    }

    /// <summary>
    /// Opens the export (README §6 CSV export): the capped count and the first row are read here, so the caller can
    /// set its headers (truncation, timing) before its first byte; the rest stream from <see cref="GridExport.RowsAsync"/>.
    /// </summary>
    /// <param name="query">The export query; its <see cref="GridQuery.Limit"/> is the row cap.</param>
    public async Task<GridExport> OpenExportAsync(GridQuery query, IReadOnlyCollection<ColumnDef> catalog, CancellationToken ct)
    {
        var sql = GridSqlBuilder.BuildExport(query, catalog);
        var started = Stopwatch.GetTimestamp();
        var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        DbDataReader? reader = null;
        try
        {
            reader = await conn.ExecuteReaderAsync(Command(sql, ct, CommandFlags.None));
            await reader.ReadAsync(ct);
            var truncated = reader.GetInt64(0) > query.Limit;
            await reader.NextResultAsync(ct);
            var hasRow = await reader.ReadAsync(ct);
            return new GridExport(conn, reader, truncated, hasRow, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch
        {
            if (reader is not null) await reader.DisposeAsync();
            await conn.DisposeAsync();
            throw;
        }
    }

    private static CommandDefinition Command(GridSql sql, CancellationToken ct, CommandFlags flags = CommandFlags.Buffered)
    {
        var p = new DynamicParameters();
        foreach (var (name, value) in sql.Parameters)
            p.Add(name, value);
        return new CommandDefinition(sql.Sql, p, flags: flags, cancellationToken: ct);
    }
}
