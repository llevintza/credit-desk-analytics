using System.Data.Common;
using System.Diagnostics;
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
    public async Task<GridBlock> ReadBlockAsync(GridQuery query, IReadOnlyCollection<ColumnDef> catalog, CancellationToken ct, GridSummary? knownSummary = null)
    {
        var sql = GridSqlBuilder.Build(query, catalog, includeSummary: knownSummary is null);
        var started = Stopwatch.GetTimestamp();
        await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        await using var reader = await conn.ExecuteReaderAsync(Command(sql, ct));

        var columns = query.Columns.Select(GridColumn.For).ToArray();
        while (await reader.ReadAsync(ct))
            for (var i = 0; i < columns.Length; i++)
                columns[i].Read(reader, i);

        if (knownSummary is not null)
            return new GridBlock(columns, knownSummary.RowCount, knownSummary.Values, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        await reader.NextResultAsync(ct);
        await reader.ReadAsync(ct);
        var rowCount = reader.GetInt32(0);
        var summary = query.Columns.Where(c => c.Aggregation != Aggregation.None)
            .Select((c, i) => new SummaryValue(c, reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1)))
            .ToArray();
        return new GridBlock(columns, rowCount, summary, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
