using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Data.App.Migrations
{
    /// <inheritdoc />
    public partial class CatalogFaceWeightedPrices : Migration
    {
        // #459 step 2 of 2: the contract step for face-weighted prices. SnapshotFaceWeightColumn (step 1, #473) shipped
        // the weight and an app that reads Aggregation.WeightedByCurrentFace; a fresh seed already writes it, but a
        // database seeded before #473 (production, SeedVersion unchanged) still says WeightedByMarketValue for these
        // rows. This flips exactly the columns the code catalog marks face-weighted, so the served catalog matches it.
        //
        // Data only: an UPDATE of 108 app.column_catalog rows, no DDL and no snapshot rewrite. Only rows that
        // differ are written, so a rerun (or a fresh database, migrated before it is seeded) touches none. Down
        // restores the |MV|-weighted value the rows had before. The app live when this runs (#473) reads both names
        // and keys its caches on them, and MetaCache re-reads the catalog within MetaCache.Revalidate, so the Total row
        // switches without a restart or cache clear. The list is frozen here; MigrationTests pins it to the catalog.
        internal const string FaceWeighted = "WeightedByCurrentFace";
        internal const string MarketValueWeighted = "WeightedByMarketValue";

        internal static readonly string[] NamedColumns =
        [
            "book_price", "price", "price_chg_1d", "price_chg_1w", "price_chg_1m",
            "worst_case_price", "best_case_price", "scenario_range",
        ];

        // The scenario price grid as of this migration (ColumnCatalog.ScenarioColumn): scn_r{m|p}{rate}_s{m|p}{spread}.
        internal static readonly int[] ScenarioRateShocksBp = [-200, -100, -50, -25, 0, 25, 50, 100, 200, 300];
        internal static readonly int[] ScenarioSpreadShocksBp = [-100, -50, -25, 0, 25, 50, 100, 200, 300, 500];

        internal static IReadOnlyList<string> Columns { get; } =
        [
            .. NamedColumns,
            .. from r in ScenarioRateShocksBp from s in ScenarioSpreadShocksBp select $"scn_r{Sign(r)}{Math.Abs(r)}_s{Sign(s)}{Math.Abs(s)}",
        ];

        internal static string SetAggregation(string value) =>
            $"UPDATE app.column_catalog SET aggregation = '{value}' WHERE aggregation <> '{value}' " +
            $"AND name IN ({string.Join(", ", Columns.Select(c => $"'{c}'"))});";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SetAggregation(FaceWeighted));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SetAggregation(MarketValueWeighted));
        }

        private static string Sign(int v) => v < 0 ? "m" : "p";
    }
}
