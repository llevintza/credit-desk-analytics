using Desk.Data.App.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Desk.Data.Tests;

/// <summary>Migrations that run raw SQL on deploy (README §14.2/§14.4).</summary>
public sealed class MigrationTests
{
    [Fact]
    public void The_sort_index_migration_is_rerun_safe_both_ways()
    {
        // #130 N3: a retried deploy, or an index someone created by hand, must not fail the migration step.
        var migration = new SnapshotSortIndexes();
        var up = migration.UpOperations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
        var down = migration.DownOperations.OfType<SqlOperation>().Select(o => o.Sql).ToList();

        Assert.Equal(SnapshotSortIndexes.SortColumns.Length, up.Count);
        Assert.All(up, sql => Assert.StartsWith("CREATE INDEX IF NOT EXISTS ix_snapshot_sort_", sql));
        Assert.Equal(up.Count, down.Count); // else Assert.All(down, …) passes on an empty Down
        Assert.All(down, sql => Assert.StartsWith("DROP INDEX IF EXISTS core.ix_snapshot_sort_", sql));
        Assert.Equal(SnapshotSortIndexes.SortColumns, up.Select(sql => sql.Split(' ')[5]["ix_snapshot_sort_".Length..]));
    }
}
