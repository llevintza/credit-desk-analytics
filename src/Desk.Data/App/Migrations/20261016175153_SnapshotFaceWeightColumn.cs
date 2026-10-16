using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Data.App.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotFaceWeightColumn : Migration
    {
        // #459: the summary's second weight, |current face| as float8, for the price columns (Aggregation.WeightedByCurrentFace).
        // Stored once per row like weight_f8 (SnapshotWeightColumn, #241), so the summary stays a plain aggregate that
        // Postgres splits across parallel workers; a per-query cast or a LATERAL would undo that (ADR-0006 addenda).
        //
        // Expand only (README §14.4): the running app never names the column and the seeder's COPY lists its columns,
        // so both keep working before and after. Derived data, filled by the ALTER itself, so no SeedVersion bump. The
        // ALTER rewrites the table under an ACCESS EXCLUSIVE lock. IF [NOT] EXISTS makes each step rerun-safe. The
        // expression is frozen here; MigrationTests pins it to GridSqlBuilder.FaceWeightExpression.
        internal const string Column = "face_weight_f8";
        internal const string Expression = "abs(current_face)::float8";

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
