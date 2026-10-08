namespace Desk.Data.Catalog;

/// <summary>How a column is stored and displayed. Drives the SQL type, the UI formatter and the API whitelist.</summary>
public enum ColumnKind
{
    Key,        // identifier (int/bigint), not aggregated
    Text,       // free text / codes
    Date,       // calendar date
    Money,      // numeric(18,2), currency amounts
    Price,      // double, 3 dp, per 100 face
    Bp,         // double, basis points, 0 dp
    Pct,        // double, ratio shown as %, 2 dp
    Ratio,      // double, plain number 2 dp (durations, convexity, WAL years, factors)
    Count,      // integer counts
    Flag,       // boolean
}

/// <summary>How the footer summary row aggregates a column over ALL filtered rows (README §6, P1).</summary>
public enum Aggregation { None, Sum, WeightedByMarketValue }

public sealed record ColumnDef(string Name, string Group, ColumnKind Kind, Aggregation Aggregation, string Header)
{
    public string SqlType => Kind switch
    {
        ColumnKind.Key => Name is "position_id" ? "bigint" : "integer",
        ColumnKind.Text => "text",
        ColumnKind.Date => "date",
        ColumnKind.Money => "numeric(18,2)",
        ColumnKind.Count => "integer",
        ColumnKind.Flag => "boolean",
        _ => "double precision",
    };
}

/// <summary>
/// The wide position snapshot's columns (README §5.3), in display order. This list is the single
/// source of truth: the core.position_snapshot DDL, app.column_catalog rows, the API whitelist
/// and the UI column definitions are all derived from it. A unit test pins the DDL in the
/// migration to this list, so changing it requires a new migration.
/// </summary>
public static class ColumnCatalog
{
    public static readonly int[] ScenarioRateShocksBp = [-200, -100, -50, -25, 0, 25, 50, 100, 200, 300];
    public static readonly int[] ScenarioSpreadShocksBp = [-100, -50, -25, 0, 25, 50, 100, 200, 300, 500];

    public static string ScenarioColumn(int rateBp, int spreadBp) =>
        $"scn_r{Sign(rateBp)}{Math.Abs(rateBp)}_s{Sign(spreadBp)}{Math.Abs(spreadBp)}";

    private static string Sign(int v) => v < 0 ? "m" : "p";

    public static readonly IReadOnlyList<ColumnDef> PositionSnapshot = Build();

    public static IReadOnlySet<string> Names { get; } =
        PositionSnapshot.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

    private static List<ColumnDef> Build()
    {
        var c = new List<ColumnDef>();
        void Add(string name, string group, ColumnKind kind, Aggregation agg, string header) =>
            c.Add(new ColumnDef(name, group, kind, agg, header));

        const string keys = "Keys";
        Add("as_of_date", keys, ColumnKind.Date, Aggregation.None, "As of");
        Add("position_id", keys, ColumnKind.Key, Aggregation.None, "Position");
        Add("portfolio_id", keys, ColumnKind.Key, Aggregation.None, "Portfolio");
        Add("fund_id", keys, ColumnKind.Key, Aggregation.None, "Fund");
        Add("bond_id", keys, ColumnKind.Key, Aggregation.None, "Bond");
        Add("deal_id", keys, ColumnKind.Key, Aggregation.None, "Deal id");
        Add("cusip", keys, ColumnKind.Text, Aggregation.None, "CUSIP");
        Add("deal_name", keys, ColumnKind.Text, Aggregation.None, "Deal");
        Add("class", keys, ColumnKind.Text, Aggregation.None, "Class");
        Add("sector", keys, ColumnKind.Text, Aggregation.None, "Sector");
        Add("sub_sector", keys, ColumnKind.Text, Aggregation.None, "Sub-sector");
        Add("vintage", keys, ColumnKind.Count, Aggregation.None, "Vintage");
        Add("rating_composite", keys, ColumnKind.Text, Aggregation.None, "Rating");
        Add("currency", keys, ColumnKind.Text, Aggregation.None, "Ccy");

        const string holding = "Holding";
        Add("face", holding, ColumnKind.Money, Aggregation.Sum, "Original face");
        Add("current_face", holding, ColumnKind.Money, Aggregation.Sum, "Current face");
        Add("factor", holding, ColumnKind.Ratio, Aggregation.WeightedByMarketValue, "Factor");
        Add("book_price", holding, ColumnKind.Price, Aggregation.WeightedByMarketValue, "Book px");
        Add("book_value", holding, ColumnKind.Money, Aggregation.Sum, "Book value");
        Add("market_value", holding, ColumnKind.Money, Aggregation.Sum, "Market value");
        Add("accrued", holding, ColumnKind.Money, Aggregation.Sum, "Accrued");
        Add("unrealized_pnl", holding, ColumnKind.Money, Aggregation.Sum, "Unrealized P&L");
        // A share of its OWN portfolio: summing across several books would exceed 100%, so no footer value.
        Add("pct_of_portfolio_mv", holding, ColumnKind.Pct, Aggregation.None, "% of port MV");

        const string pricing = "Pricing";
        Add("price", pricing, ColumnKind.Price, Aggregation.WeightedByMarketValue, "Price");
        Add("price_chg_1d", pricing, ColumnKind.Price, Aggregation.WeightedByMarketValue, "Px chg 1d");
        Add("price_chg_1w", pricing, ColumnKind.Price, Aggregation.WeightedByMarketValue, "Px chg 1w");
        Add("price_chg_1m", pricing, ColumnKind.Price, Aggregation.WeightedByMarketValue, "Px chg 1m");
        Add("yield", pricing, ColumnKind.Pct, Aggregation.WeightedByMarketValue, "Yield");
        Add("spread_bp", pricing, ColumnKind.Bp, Aggregation.WeightedByMarketValue, "Spread");
        Add("oas_bp", pricing, ColumnKind.Bp, Aggregation.WeightedByMarketValue, "OAS");
        Add("dm_bp", pricing, ColumnKind.Bp, Aggregation.WeightedByMarketValue, "DM");
        Add("spread_chg_1d_bp", pricing, ColumnKind.Bp, Aggregation.WeightedByMarketValue, "Sprd chg 1d");
        Add("spread_chg_1w_bp", pricing, ColumnKind.Bp, Aggregation.WeightedByMarketValue, "Sprd chg 1w");
        Add("spread_chg_1m_bp", pricing, ColumnKind.Bp, Aggregation.WeightedByMarketValue, "Sprd chg 1m");
        Add("z_spread_bp", pricing, ColumnKind.Bp, Aggregation.WeightedByMarketValue, "Z-spread");
        Add("price_source", pricing, ColumnKind.Text, Aggregation.None, "Px source");
        Add("vendor_dispersion_bp", pricing, ColumnKind.Bp, Aggregation.WeightedByMarketValue, "Vendor disp.");

        const string rates = "Rate risk";
        Add("mod_duration", rates, ColumnKind.Ratio, Aggregation.WeightedByMarketValue, "Mod dur");
        Add("eff_duration", rates, ColumnKind.Ratio, Aggregation.WeightedByMarketValue, "Eff dur");
        Add("convexity", rates, ColumnKind.Ratio, Aggregation.WeightedByMarketValue, "Convexity");
        Add("dv01", rates, ColumnKind.Money, Aggregation.Sum, "DV01");
        foreach (var t in new[] { 2, 5, 10, 20, 30 })
            Add($"krd_{t}y", rates, ColumnKind.Money, Aggregation.Sum, $"KR DV01 {t}y");

        const string credit = "Credit risk";
        Add("rating_sp", credit, ColumnKind.Text, Aggregation.None, "S&P");
        Add("rating_moodys", credit, ColumnKind.Text, Aggregation.None, "Moody's");
        Add("rating_fitch", credit, ColumnKind.Text, Aggregation.None, "Fitch");
        Add("spread_duration", credit, ColumnKind.Ratio, Aggregation.WeightedByMarketValue, "Spread dur");
        Add("cs01", credit, ColumnKind.Money, Aggregation.Sum, "CS01");
        Add("jtd", credit, ColumnKind.Money, Aggregation.Sum, "Jump to default");
        Add("expected_loss_pct", credit, ColumnKind.Pct, Aggregation.WeightedByMarketValue, "Exp. loss");
        Add("attachment_pct", credit, ColumnKind.Pct, Aggregation.WeightedByMarketValue, "Attach");
        Add("detachment_pct", credit, ColumnKind.Pct, Aggregation.WeightedByMarketValue, "Detach");
        Add("credit_enhancement_pct", credit, ColumnKind.Pct, Aggregation.WeightedByMarketValue, "Credit enh.");

        const string cashflow = "Cash flow";
        Add("wal", cashflow, ColumnKind.Ratio, Aggregation.WeightedByMarketValue, "WAL");
        Add("window_start", cashflow, ColumnKind.Date, Aggregation.None, "Window start");
        Add("window_end", cashflow, ColumnKind.Date, Aggregation.None, "Window end");
        Add("next_pay_date", cashflow, ColumnKind.Date, Aggregation.None, "Next pay");
        Add("coupon_current", cashflow, ColumnKind.Pct, Aggregation.WeightedByMarketValue, "Coupon");
        Add("coupon_next", cashflow, ColumnKind.Pct, Aggregation.WeightedByMarketValue, "Next coupon");

        const string collateral = "Collateral";
        foreach (var (n, h) in new[]
                 {
                     ("cpr_1m", "CPR 1m"), ("cpr_3m", "CPR 3m"), ("cpr_12m", "CPR 12m"),
                     ("cdr_1m", "CDR 1m"), ("cdr_3m", "CDR 3m"), ("cdr_12m", "CDR 12m"),
                     ("severity_3m", "Sev 3m"), ("severity_12m", "Sev 12m"),
                     ("dq_30", "DQ 30"), ("dq_60", "DQ 60"), ("dq_90plus", "DQ 90+"),
                     ("foreclosure_pct", "Foreclosure"), ("reo_pct", "REO"), ("wac", "WAC"),
                 })
            Add(n, collateral, ColumnKind.Pct, Aggregation.WeightedByMarketValue, h);
        Add("wala", collateral, ColumnKind.Ratio, Aggregation.WeightedByMarketValue, "WALA (m)");
        Add("ltv_wavg", collateral, ColumnKind.Pct, Aggregation.WeightedByMarketValue, "LTV");
        Add("fico_wavg", collateral, ColumnKind.Ratio, Aggregation.WeightedByMarketValue, "FICO");
        Add("loan_count", collateral, ColumnKind.Count, Aggregation.None, "Loans");

        const string tests = "Deal tests";
        Add("oc_test_cushion", tests, ColumnKind.Pct, Aggregation.WeightedByMarketValue, "OC cushion");
        Add("ic_test_cushion", tests, ColumnKind.Pct, Aggregation.WeightedByMarketValue, "IC cushion");
        Add("warf", tests, ColumnKind.Ratio, Aggregation.WeightedByMarketValue, "WARF");
        Add("diversity_score", tests, ColumnKind.Ratio, Aggregation.WeightedByMarketValue, "Diversity");
        Add("ccc_bucket_pct", tests, ColumnKind.Pct, Aggregation.WeightedByMarketValue, "CCC bucket");

        const string pnl = "P&L attribution (MTD)";
        foreach (var (n, h) in new[]
                 {
                     ("pnl_carry", "Carry"), ("pnl_roll_down", "Roll-down"), ("pnl_rates", "Rates"),
                     ("pnl_spread", "Spread"), ("pnl_idiosyncratic", "Idiosyncratic"), ("pnl_fx", "FX"),
                     ("pnl_residual", "Residual"), ("pnl_total_mtd", "Total MTD"),
                 })
            Add(n, pnl, ColumnKind.Money, Aggregation.Sum, h);

        const string scenarios = "Scenarios (price)";
        foreach (var r in ScenarioRateShocksBp)
        foreach (var s in ScenarioSpreadShocksBp)
            Add(ScenarioColumn(r, s), scenarios, ColumnKind.Price, Aggregation.WeightedByMarketValue,
                $"R{(r >= 0 ? "+" : "")}{r} S{(s >= 0 ? "+" : "")}{s}");

        const string stress = "Stress";
        Add("worst_case_price", stress, ColumnKind.Price, Aggregation.WeightedByMarketValue, "Worst px");
        Add("best_case_price", stress, ColumnKind.Price, Aggregation.WeightedByMarketValue, "Best px");
        Add("scenario_range", stress, ColumnKind.Price, Aggregation.WeightedByMarketValue, "Px range");
        Add("stress_loss_mv", stress, ColumnKind.Money, Aggregation.Sum, "Stress loss");

        const string flags = "Flags";
        Add("watchlist_flag", flags, ColumnKind.Flag, Aggregation.None, "Watchlist");
        Add("restricted_flag", flags, ColumnKind.Flag, Aggregation.None, "Restricted");
        Add("comment_count", flags, ColumnKind.Count, Aggregation.None, "Comments");
        Add("last_trade_date", flags, ColumnKind.Date, Aggregation.None, "Last trade");
        Add("analyst", flags, ColumnKind.Text, Aggregation.None, "Analyst");

        return c;
    }

    /// <summary>DDL for core.position_snapshot rendered from the catalog (pinned into the migration by a test).</summary>
    public static string PositionSnapshotDdl()
    {
        var cols = PositionSnapshot.Select(c =>
        {
            var notNull = c.Kind == ColumnKind.Key || c.Name is "as_of_date" or "cusip" or "deal_name" or "sector";
            return $"    {c.Name} {c.SqlType}{(notNull ? " NOT NULL" : "")}";
        });
        return "CREATE TABLE core.position_snapshot (\n" + string.Join(",\n", cols) +
               ",\n    PRIMARY KEY (as_of_date, position_id)\n);";
    }
}
