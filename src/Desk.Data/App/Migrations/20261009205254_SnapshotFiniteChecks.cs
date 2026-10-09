using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Data.App.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotFiniteChecks : Migration
    {
        // #304, follow-up to #192/#286: the numeric(18,2) measures already reject ±Infinity through their typmod, but the
        // float8 measures accept 'Infinity' and '-Infinity'. In a weighted average an Infinity turns back into NaN
        // (Inf × 0 on a zero-weight row, or +Inf mixed with -Inf), the failure #192 fixed. So each float8 measure's
        // _not_nan CHECK is replaced by a finite one. The range test also rejects NaN (float8 NaN sorts above every
        // number, Infinity included), and a NULL column makes the CHECK NULL, which passes. The numeric _not_nan checks
        // stay as they are.
        //
        // The column list is SnapshotNanChecks.Float8Measures, frozen when that migration was applied; MigrationTests
        // pins it to the catalog, so a new float8 measure still needs its own migration.
        //
        // One ALTER TABLE, one validating scan under an ACCESS EXCLUSIVE lock (measured at scale 1.0 in the PR). Each
        // DROP CONSTRAINT IF EXISTS makes the step rerun-safe. Down restores the #286 _not_nan checks. No data rewritten.
        internal static IReadOnlyList<string> Columns => SnapshotNanChecks.Float8Measures;

        internal static string ConstraintName(string column) => $"ck_snapshot_{column}_finite";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var checks = Columns.Select(c =>
                $"DROP CONSTRAINT IF EXISTS {SnapshotNanChecks.ConstraintName(c)}, DROP CONSTRAINT IF EXISTS {ConstraintName(c)}, " +
                $"ADD CONSTRAINT {ConstraintName(c)} CHECK ({c} > '-Infinity'::float8 AND {c} < 'Infinity'::float8)");
            migrationBuilder.Sql("ALTER TABLE core.position_snapshot\n    " + string.Join(",\n    ", checks) + ";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var checks = Columns.Select(c =>
                $"DROP CONSTRAINT IF EXISTS {ConstraintName(c)}, DROP CONSTRAINT IF EXISTS {SnapshotNanChecks.ConstraintName(c)}, " +
                $"ADD CONSTRAINT {SnapshotNanChecks.ConstraintName(c)} CHECK ({c} <> 'NaN'::float8)");
            migrationBuilder.Sql("ALTER TABLE core.position_snapshot\n    " + string.Join(",\n    ", checks) + ";");
        }
    }
}
