using Desk.Data.Catalog;

namespace Desk.Data.Grid;

/// <summary>
/// The named presets every user starts with on the positions page (README §6 P1). Users save their own on top;
/// these are code, not rows, so a catalog change updates them for everyone. "Risk" is the payload-budget preset.
/// </summary>
public static class BuiltInPresets
{
    public const string Page = "positions";

    private static readonly string[] Identity = ["deal_name", "class", "cusip", "sector", "rating_composite"];

    public static readonly IReadOnlyList<string> Risk =
    [
        .. Identity,
        "current_face", "market_value", "unrealized_pnl", "pct_of_portfolio_mv",
        "price", "price_chg_1d", "yield", "spread_bp", "oas_bp", "dm_bp", "z_spread_bp", "spread_chg_1d_bp", "vendor_dispersion_bp",
        "mod_duration", "eff_duration", "convexity", "dv01", "krd_2y", "krd_5y", "krd_10y", "krd_20y", "krd_30y",
        "spread_duration", "cs01", "jtd", "expected_loss_pct", "credit_enhancement_pct",
        "wal", "coupon_current",
        "pnl_carry", "pnl_rates", "pnl_spread", "pnl_total_mtd",
        "worst_case_price", "stress_loss_mv",
        "watchlist_flag",
    ];

    public static readonly IReadOnlyList<string> Surveillance =
    [
        .. Identity, "sub_sector", "vintage", "current_face", "market_value", "factor",
        "cpr_1m", "cpr_3m", "cpr_12m", "cdr_1m", "cdr_3m", "cdr_12m", "severity_3m", "severity_12m",
        "dq_30", "dq_60", "dq_90plus", "foreclosure_pct", "reo_pct", "wac", "wala", "ltv_wavg", "fico_wavg", "loan_count",
        "oc_test_cushion", "ic_test_cushion", "warf", "diversity_score", "ccc_bucket_pct", "credit_enhancement_pct",
    ];

    public static readonly IReadOnlyList<string> Scenarios =
    [
        .. Identity, "market_value", "price",
        .. ColumnCatalog.ScenarioRateShocksBp.SelectMany(r => ColumnCatalog.ScenarioSpreadShocksBp.Select(s => ColumnCatalog.ScenarioColumn(r, s))),
        "worst_case_price", "best_case_price", "scenario_range", "stress_loss_mv",
    ];

    /// <summary>Every catalog column except the internal ids and the as-of date (shown in the header instead).</summary>
    public static readonly IReadOnlyList<string> All = ColumnCatalog.PositionSnapshot
        .Where(c => c.Name is not ("as_of_date" or "position_id" or "portfolio_id" or "fund_id" or "bond_id" or "deal_id"))
        .Select(c => c.Name)
        .ToArray();

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ByName = new Dictionary<string, IReadOnlyList<string>>
    {
        ["Risk"] = Risk,
        ["Surveillance"] = Surveillance,
        ["Scenarios"] = Scenarios,
        ["All"] = All,
    };
}
