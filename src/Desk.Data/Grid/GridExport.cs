using System.Data.Common;

namespace Desk.Data.Grid;

/// <summary>
/// An open export (<see cref="GridRepository.OpenExportAsync"/>): what the headers need is known, and the rows stream
/// from the open reader. Disposing it closes the reader and the connection.
/// </summary>
/// <param name="Truncated">More rows match than the cap: only the cap's rows stream (#277).</param>
/// <param name="DbMs">Time to the first row (or to the end of an empty result).</param>
public sealed class GridExport(DbConnection connection, DbDataReader reader, bool truncated, bool hasRow, double dbMs) : IAsyncDisposable
{
    public bool Truncated => truncated;

    public double DbMs => dbMs;

    /// <summary>Each row as it arrives, starting with the one already read; the same reader, advanced.</summary>
    public async IAsyncEnumerable<DbDataReader> RowsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!hasRow) yield break;
        yield return reader;
        while (await reader.ReadAsync(ct))
            yield return reader;
    }

    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync();
        await connection.DisposeAsync();
    }
}
