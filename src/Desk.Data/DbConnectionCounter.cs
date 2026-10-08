using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Desk.Data;

/// <summary>
/// Counts database connections opened through EF Core. Lets tests prove that a code path (maintenance mode,
/// cache hits, health probes) never touches the database (README §11), and is cheap enough to leave on.
/// </summary>
public sealed class DbConnectionCounter : DbConnectionInterceptor
{
    private long _opened;

    public long Opened => Interlocked.Read(ref _opened);

    /// <summary>Counts a connection opened outside EF (Dapper reads through the data-source registry).</summary>
    public void Record() => Interlocked.Increment(ref _opened);

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        Interlocked.Increment(ref _opened);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _opened);
        return Task.CompletedTask;
    }
}
