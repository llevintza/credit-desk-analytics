using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Data.App.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotWeightColumn : Migration
    {
        // #241: the summary's weight, |market value| as float8, stored once per row instead of computed per query. The
        // builder used to compute it in a CROSS JOIN LATERAL (... OFFSET 0) (ADR-0006: the numeric→float8 cast once per
        // row, not once per aggregate). That fence costs a nested loop per row and keeps Postgres from aggregating in
        // parallel workers: the All preset's whole-book summary (174 aggregates over ~20k rows) ran in the leader alone.
        // With a stored column the summary is a plain aggregate over the table, and the planner picks a parallel
        // partial aggregate on its own (ADR-0006 addendum, measured at scale 1.0).
        //
        // Expand only (README §14.4): the running app never names the column and the seeder's COPY lists its columns,
        // so both keep working before and after. Derived data, filled by the ALTER itself, so no SeedVersion bump. The
        // ALTER rewrites the table under an ACCESS EXCLUSIVE lock (1.8 s at scale 1.0, ~40k rows). IF [NOT] EXISTS makes
        // each step rerun-safe. The expression is frozen here; MigrationTests pins it to GridSqlBuilder.WeightExpression.
        internal const string Column = "weight_f8";
        internal const string Expression = "abs(market_value)::float8";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"ALTER TABLE core.position_snapshot ADD COLUMN IF NOT EXISTS {Column} double precision GENERATED ALWAYS AS ({Expression}) STORED;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"ALTER TABLE core.position_snapshot DROP COLUMN IF EXISTS {Column};");
        }
    }
}
