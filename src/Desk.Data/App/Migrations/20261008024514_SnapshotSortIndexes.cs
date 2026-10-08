using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Data.App.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotSortIndexes : Migration
    {
        // P1 sort indexes (README §6, ADR-0008). Chosen from EXPLAIN (ANALYZE, BUFFERS) at scale 1.0: the first block
        // sorted by market value went from a 7,800-page seq scan + top-N sort (26 ms) to a 200-row index scan
        // (0.2 ms). Plain ascending btrees: the builder orders the position_id tie-breaker in the first key's
        // direction with default NULL placement, so a backward scan serves DESC. ~1.6 MB each at scale 1.0.
        //
        // Locking (#130 N3): a plain CREATE INDEX takes a SHARE lock on core.position_snapshot, which blocks writes (the
        // seeder) but not reads (the running app) while it builds. At scale 1.0 each build takes 19-43 ms, about 0.1 s
        // for all four. IF NOT EXISTS makes the step rerun-safe (a retried deploy, or an index created by hand), and
        // the seeder's runtime is unchanged by the indexes (about 8 s at scale 1.0 with or without them; budget < 90 s).
        internal static readonly string[] SortColumns = ["market_value", "spread_bp", "dv01", "deal_name"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var c in SortColumns)
                migrationBuilder.Sql($"CREATE INDEX IF NOT EXISTS ix_snapshot_sort_{c} ON core.position_snapshot (as_of_date, {c}, position_id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var c in SortColumns)
                migrationBuilder.Sql($"DROP INDEX IF EXISTS core.ix_snapshot_sort_{c};");
        }
    }
}
