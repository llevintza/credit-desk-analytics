using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Data.App.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotNanChecks : Migration
    {
        // #192, README §8 "never NaN": Postgres accepts 'NaN' in both numeric and double precision columns. One NaN
        // market_value makes every weighted average NaN (abs('NaN')::float8 is NaN), and one NaN in a measure turns that
        // column's SUM or weighted average into NaN; a NaN numeric can't even be read into a decimal. So every column the
        // summary row aggregates (ColumnCatalog: Sum or WeightedByMarketValue, which includes market_value, the weight)
        // rejects NaN at write time. NULL still passes (a CHECK that is NULL is satisfied), so "missing" keeps working.
        // Fail closed at write time instead of nullif() in the summary SQL: no hot-path change, zero query cost.
        //
        // Frozen lists, not read from the catalog at run time: an applied migration must not change meaning later.
        // MigrationTests pins them to the catalog's aggregated columns, so a new measure needs a new migration.
        //
        // One ALTER TABLE: Postgres validates all the new checks in a single scan, under an ACCESS EXCLUSIVE lock on
        // core.position_snapshot (reads wait too). Measured at scale 1.0 in the PR. DROP CONSTRAINT IF EXISTS before each
        // ADD makes the step rerun-safe (a retried deploy, or a constraint created by hand). No data is rewritten.
        internal static readonly string[] NumericMeasures =
        [
            "face", "current_face", "book_value", "market_value", "accrued", "unrealized_pnl", "dv01", "krd_2y", "krd_5y",
            "krd_10y", "krd_20y", "krd_30y", "cs01", "jtd", "pnl_carry", "pnl_roll_down", "pnl_rates", "pnl_spread",
            "pnl_idiosyncratic", "pnl_fx", "pnl_residual", "pnl_total_mtd", "stress_loss_mv"
        ];

        internal static readonly string[] Float8Measures =
        [
            "factor", "book_price", "price", "price_chg_1d", "price_chg_1w", "price_chg_1m", "yield", "spread_bp",
            "oas_bp", "dm_bp", "spread_chg_1d_bp", "spread_chg_1w_bp", "spread_chg_1m_bp", "z_spread_bp",
            "vendor_dispersion_bp", "mod_duration", "eff_duration", "convexity", "spread_duration", "expected_loss_pct",
            "attachment_pct", "detachment_pct", "credit_enhancement_pct", "wal", "coupon_current", "coupon_next", "cpr_1m",
            "cpr_3m", "cpr_12m", "cdr_1m", "cdr_3m", "cdr_12m", "severity_3m", "severity_12m", "dq_30", "dq_60",
            "dq_90plus", "foreclosure_pct", "reo_pct", "wac", "wala", "ltv_wavg", "fico_wavg", "oc_test_cushion",
            "ic_test_cushion", "warf", "diversity_score", "ccc_bucket_pct", "scn_rm200_sm100", "scn_rm200_sm50",
            "scn_rm200_sm25", "scn_rm200_sp0", "scn_rm200_sp25", "scn_rm200_sp50", "scn_rm200_sp100", "scn_rm200_sp200",
            "scn_rm200_sp300", "scn_rm200_sp500", "scn_rm100_sm100", "scn_rm100_sm50", "scn_rm100_sm25", "scn_rm100_sp0",
            "scn_rm100_sp25", "scn_rm100_sp50", "scn_rm100_sp100", "scn_rm100_sp200", "scn_rm100_sp300", "scn_rm100_sp500",
            "scn_rm50_sm100", "scn_rm50_sm50", "scn_rm50_sm25", "scn_rm50_sp0", "scn_rm50_sp25", "scn_rm50_sp50",
            "scn_rm50_sp100", "scn_rm50_sp200", "scn_rm50_sp300", "scn_rm50_sp500", "scn_rm25_sm100", "scn_rm25_sm50",
            "scn_rm25_sm25", "scn_rm25_sp0", "scn_rm25_sp25", "scn_rm25_sp50", "scn_rm25_sp100", "scn_rm25_sp200",
            "scn_rm25_sp300", "scn_rm25_sp500", "scn_rp0_sm100", "scn_rp0_sm50", "scn_rp0_sm25", "scn_rp0_sp0",
            "scn_rp0_sp25", "scn_rp0_sp50", "scn_rp0_sp100", "scn_rp0_sp200", "scn_rp0_sp300", "scn_rp0_sp500",
            "scn_rp25_sm100", "scn_rp25_sm50", "scn_rp25_sm25", "scn_rp25_sp0", "scn_rp25_sp25", "scn_rp25_sp50",
            "scn_rp25_sp100", "scn_rp25_sp200", "scn_rp25_sp300", "scn_rp25_sp500", "scn_rp50_sm100", "scn_rp50_sm50",
            "scn_rp50_sm25", "scn_rp50_sp0", "scn_rp50_sp25", "scn_rp50_sp50", "scn_rp50_sp100", "scn_rp50_sp200",
            "scn_rp50_sp300", "scn_rp50_sp500", "scn_rp100_sm100", "scn_rp100_sm50", "scn_rp100_sm25", "scn_rp100_sp0",
            "scn_rp100_sp25", "scn_rp100_sp50", "scn_rp100_sp100", "scn_rp100_sp200", "scn_rp100_sp300", "scn_rp100_sp500",
            "scn_rp200_sm100", "scn_rp200_sm50", "scn_rp200_sm25", "scn_rp200_sp0", "scn_rp200_sp25", "scn_rp200_sp50",
            "scn_rp200_sp100", "scn_rp200_sp200", "scn_rp200_sp300", "scn_rp200_sp500", "scn_rp300_sm100",
            "scn_rp300_sm50", "scn_rp300_sm25", "scn_rp300_sp0", "scn_rp300_sp25", "scn_rp300_sp50", "scn_rp300_sp100",
            "scn_rp300_sp200", "scn_rp300_sp300", "scn_rp300_sp500", "worst_case_price", "best_case_price",
            "scenario_range"
        ];

        internal static string ConstraintName(string column) => $"ck_snapshot_{column}_not_nan";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var checks = NumericMeasures.Select(c => (c, "numeric")).Concat(Float8Measures.Select(c => (c, "float8")))
                .Select(x => $"DROP CONSTRAINT IF EXISTS {ConstraintName(x.c)}, " +
                             $"ADD CONSTRAINT {ConstraintName(x.c)} CHECK ({x.c} <> 'NaN'::{x.Item2})");
            migrationBuilder.Sql("ALTER TABLE core.position_snapshot\n    " + string.Join(",\n    ", checks) + ";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var drops = NumericMeasures.Concat(Float8Measures).Select(c => $"DROP CONSTRAINT IF EXISTS {ConstraintName(c)}");
            migrationBuilder.Sql("ALTER TABLE core.position_snapshot\n    " + string.Join(",\n    ", drops) + ";");
        }
    }
}
