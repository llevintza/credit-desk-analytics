namespace Desk.Data.Insights;

/// <summary>
/// One grid's query. <see cref="Pivot"/> set: the SQL returns long rows (row key, column key, value) and the grid
/// is their cross-tab with those columns. Otherwise the SQL returns the grid as is, headed by <see cref="Columns"/>.
/// </summary>
/// <param name="Formats">One display format per header (the label column included).</param>
public sealed record InsightSpec(string Source, string Id, string Title, string Sql, string[] Columns, string[] Formats, string[]? Pivot = null)
{
    /// <summary>The cross-tab's value format (every value column shares it).</summary>
    public string ValueFormat => Formats[^1];
}

/// <summary>
/// The 20 P3 grids, grouped by source (README §6 P3). Every query takes exactly two parameters, <c>@asOf</c> and
/// <c>@portfolios</c>, and scopes to the caller's book (<c>portfolio_id = ANY(@portfolios)</c>, ADR-0021): market
/// data is shown only for what the book holds, so an empty scope is zero rows everywhere. Nothing here comes from the
/// client but those two values. Children are aggregated before a parent is joined (README §6 P3 grain rule).
/// </summary>
public static class InsightCatalog
{
    public const string Core = "core";
    public const string Market = "market";
    public const string Surveillance = "surveillance";
    public const string Pricing = "pricing";
    public const string Reference = "reference";

    public static readonly IReadOnlyList<string> Sources = [Core, Market, Surveillance, Pricing, Reference];

    /// <summary>The connection setting each source reads through (README §5.1).</summary>
    public static string ConnectionName(string source) => source switch
    {
        Core => ConnectionStrings.Core,
        Market => ConnectionStrings.Market,
        Surveillance => ConnectionStrings.Surveillance,
        Pricing => ConnectionStrings.Pricing,
        Reference => ConnectionStrings.Reference,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown insights source."),
    };

    private const string Pos = "core.position_snapshot WHERE as_of_date = @asOf AND portfolio_id = ANY(@portfolios)";
    private const string PosWithDeal =
        "core.position_snapshot p JOIN core.deal d USING (deal_id) WHERE p.as_of_date = @asOf AND p.portfolio_id = ANY(@portfolios)";
    private const string SectorOrder = "array_position(ARRAY['CLO','RMBS','CMBS','ABS','CRT'], {0})";
    private static string BySector(string column) => string.Format(SectorOrder, column);

    private static readonly string[] RatingBuckets = ["AAA", "AA", "A", "BBB", "BB", "≤B/NR"];
    private static readonly string[] Ratings = ["AAA", "AA", "A", "BBB", "BB", "B", "CCC"];

    public static readonly IReadOnlyList<InsightSpec> All =
    [
        // ---------- core (6) ----------
        new(Core, "mv_sector_rating", "Market value by sector × rating",
            $"""
            SELECT sector, CASE WHEN rating_composite IN ('AAA','AA','A','BBB','BB') THEN rating_composite ELSE '≤B/NR' END,
                   sum(market_value)
            FROM {Pos}
            GROUP BY 1, 2
            ORDER BY {BySector("sector")}, 2
            """,
            ["Sector", .. RatingBuckets], ["text", .. RatingBuckets.Select(_ => "money0")], RatingBuckets),

        new(Core, "dv01_sector_duration", "DV01 by sector × effective duration (yrs)",
            $"""
            SELECT sector,
                   CASE WHEN eff_duration IS NULL THEN 'n/a' WHEN eff_duration < 2 THEN '0–2' WHEN eff_duration < 4 THEN '2–4'
                        WHEN eff_duration < 6 THEN '4–6' WHEN eff_duration < 8 THEN '6–8' ELSE '8+' END,
                   sum(dv01)
            FROM {Pos}
            GROUP BY 1, 2
            ORDER BY {BySector("sector")}, 2
            """,
            ["Sector", "0–2", "2–4", "4–6", "6–8", "8+"], ["text", "money0", "money0", "money0", "money0", "money0"], ["0–2", "2–4", "4–6", "6–8", "8+"]),

        new(Core, "pnl_attribution_portfolio", "MTD P&L attribution by portfolio",
            $"""
            WITH p AS (
                SELECT portfolio_id, sum(pnl_carry) carry, sum(pnl_roll_down) roll, sum(pnl_rates) rates,
                       sum(pnl_spread) spread, sum(pnl_idiosyncratic) idio, sum(pnl_total_mtd) total
                FROM {Pos}
                GROUP BY portfolio_id)
            SELECT pf.name, p.carry, p.roll, p.rates, p.spread, p.idio, p.total
            FROM p JOIN core.portfolio pf USING (portfolio_id)
            ORDER BY p.portfolio_id
            """,
            ["Portfolio", "Carry", "Roll-down", "Rates", "Spread", "Idio", "Total"], ["text", "money0", "money0", "money0", "money0", "money0", "money0"]),

        new(Core, "top_issuers_mv", "Top 5 issuers by market value",
            $"""
            WITH by_issuer AS (
                SELECT d.issuer_id, sum(p.market_value) mv, count(*) n
                FROM {PosWithDeal}
                GROUP BY d.issuer_id),
            ranked AS (SELECT issuer_id, mv, n, mv / NULLIF(sum(mv) OVER (), 0) share FROM by_issuer)
            SELECT i.name, r.mv, r.share, r.n
            FROM ranked r JOIN reference.issuer i USING (issuer_id)
            ORDER BY r.mv DESC NULLS LAST, r.issuer_id
            LIMIT 5
            """,
            ["Issuer", "MV", "% of book", "Positions"], ["text", "money0", "pct1", "int"]),

        new(Core, "vintage_concentration", "Concentration by vintage",
            $"""
            WITH v AS (
                SELECT CASE WHEN vintage <= 2007 THEN '≤2007' WHEN vintage <= 2019 THEN '2017–2019' ELSE vintage::text END bucket,
                       min(vintage) first_year, sum(market_value) mv, count(*) n
                FROM {Pos}
                GROUP BY 1)
            SELECT bucket, mv, mv / NULLIF(sum(mv) OVER (), 0), n
            FROM v
            ORDER BY first_year
            """,
            ["Vintage", "MV", "% of book", "Positions"], ["text", "money0", "pct1", "int"]),

        // Deal original balance is a deal (parent) field: summed over the distinct deals held, never per position
        // (the fan-out double count, README §6 P3).
        new(Core, "watchlist_sector", "Watchlist by sector",
            $"""
            WITH p AS (SELECT * FROM {Pos}),
            s AS (
                SELECT sector, sum(market_value) mv, count(*) FILTER (WHERE watchlist_flag) wl_n,
                       coalesce(sum(market_value) FILTER (WHERE watchlist_flag), 0) wl_mv
                FROM p GROUP BY sector),
            d AS (
                SELECT sector, count(*) n, sum(original_balance) orig
                FROM core.deal WHERE deal_id IN (SELECT deal_id FROM p)
                GROUP BY sector)
            SELECT s.sector, coalesce(d.n, 0), d.orig, s.wl_n, s.wl_mv, s.wl_mv / NULLIF(s.mv, 0)
            FROM s LEFT JOIN d USING (sector)
            ORDER BY {BySector("s.sector")}
            """,
            ["Sector", "Deals held", "Deal orig. bal.", "Watchlist", "Watchlist MV", "Watchlist % MV"], ["text", "int", "money0", "int", "money0", "pct1"]),

        // ---------- market (3): index levels for the sector × rating cells the book holds ----------
        new(Market, "spread_change_1m", "Spread change 1M (bp) by sector × rating",
            $"""
            WITH t AS (SELECT max(as_of_date) t1 FROM market.spread_index WHERE as_of_date <= @asOf),
            t0 AS (SELECT max(s.as_of_date) t0 FROM market.spread_index s, t WHERE s.as_of_date <= t.t1 - 30),
            held AS (SELECT DISTINCT sector, rating_composite rating FROM {Pos})
            SELECT s1.sector, s1.rating, s1.spread_bp - s0.spread_bp
            FROM t, t0, market.spread_index s1
            JOIN market.spread_index s0 ON s0.sector = s1.sector AND s0.rating = s1.rating
            WHERE s1.as_of_date = t.t1 AND s0.as_of_date = t0.t0 AND (s1.sector, s1.rating) IN (SELECT sector, rating FROM held)
            ORDER BY {BySector("s1.sector")}
            """,
            ["Sector", .. Ratings], ["text", .. Ratings.Select(_ => "bp1")], Ratings),

        new(Market, "curve_moves", "Curve moves (level, change in bp)",
            $"""
            WITH t AS (SELECT max(as_of_date) t1 FROM market.rate_curve WHERE as_of_date <= @asOf),
            d AS (SELECT t.t1,
                         (SELECT max(as_of_date) FROM market.rate_curve WHERE as_of_date <= t.t1 - 7) w1,
                         (SELECT max(as_of_date) FROM market.rate_curve WHERE as_of_date <= t.t1 - 30) m1
                  FROM t)
            SELECT CASE WHEN c.tenor_months < 12 THEN c.tenor_months || 'M' ELSE (c.tenor_months / 12) || 'Y' END,
                   max(c.rate) FILTER (WHERE c.curve = 'UST' AND c.as_of_date = d.t1),
                   (max(c.rate) FILTER (WHERE c.curve = 'UST' AND c.as_of_date = d.t1) - max(c.rate) FILTER (WHERE c.curve = 'UST' AND c.as_of_date = d.w1)) * 10000,
                   (max(c.rate) FILTER (WHERE c.curve = 'UST' AND c.as_of_date = d.t1) - max(c.rate) FILTER (WHERE c.curve = 'UST' AND c.as_of_date = d.m1)) * 10000,
                   max(c.rate) FILTER (WHERE c.curve = 'SOFR' AND c.as_of_date = d.t1),
                   (max(c.rate) FILTER (WHERE c.curve = 'SOFR' AND c.as_of_date = d.t1) - max(c.rate) FILTER (WHERE c.curve = 'SOFR' AND c.as_of_date = d.m1)) * 10000
            FROM market.rate_curve c, d
            WHERE c.as_of_date IN (d.t1, d.w1, d.m1) AND c.tenor_months IN (3, 24, 60, 120, 360)
              AND EXISTS (SELECT 1 FROM {Pos})
            GROUP BY c.tenor_months
            ORDER BY c.tenor_months
            """,
            ["Tenor", "UST", "UST Δ1W", "UST Δ1M", "SOFR", "SOFR Δ1M"], ["text", "pct2", "bp1", "bp1", "pct2", "bp1"]),

        new(Market, "spread_percentile_2y", "Spread percentile vs 2Y by sector × rating",
            $"""
            WITH t AS (SELECT max(as_of_date) t1 FROM market.spread_index WHERE as_of_date <= @asOf),
            held AS (SELECT DISTINCT sector, rating_composite rating FROM {Pos}),
            cur AS (
                SELECT s.sector, s.rating, s.spread_bp FROM market.spread_index s, t
                WHERE s.as_of_date = t.t1 AND (s.sector, s.rating) IN (SELECT sector, rating FROM held))
            SELECT c.sector, c.rating, avg((h.spread_bp <= c.spread_bp)::int)
            FROM cur c, t, market.spread_index h
            WHERE h.sector = c.sector AND h.rating = c.rating AND h.as_of_date > t.t1 - 730 AND h.as_of_date <= t.t1
            GROUP BY c.sector, c.rating
            ORDER BY {BySector("c.sector")}
            """,
            ["Sector", .. Ratings], ["text", .. Ratings.Select(_ => "pct0")], Ratings),

        // ---------- surveillance (5): each held deal's latest remittance on or before the as-of date ----------
        new(Surveillance, "dq60_sector_vintage", "60+ day delinquency by sector × vintage (loan-weighted)",
            $"""
            WITH r AS (
                SELECT DISTINCT ON (deal_id) deal_id, loan_count, dq_60 + dq_90plus dq
                FROM surveillance.deal_remit
                WHERE period <= @asOf AND deal_id IN (SELECT deal_id FROM {Pos})
                ORDER BY deal_id, period DESC)
            SELECT d.sector,
                   CASE WHEN d.vintage <= 2007 THEN '≤2007' WHEN d.vintage <= 2019 THEN '2017–19' WHEN d.vintage <= 2021 THEN '2020–21'
                        WHEN d.vintage <= 2023 THEN '2022–23' ELSE '2024+' END,
                   sum(r.dq * r.loan_count) / NULLIF(sum(r.loan_count), 0)
            FROM r JOIN core.deal d USING (deal_id)
            GROUP BY 1, 2
            ORDER BY {BySector("d.sector")}
            """,
            ["Sector", "≤2007", "2017–19", "2020–21", "2022–23", "2024+"], ["text", "pct2", "pct2", "pct2", "pct2", "pct2"], ["≤2007", "2017–19", "2020–21", "2022–23", "2024+"]),

        new(Surveillance, "cpr_cdr_servicer", "CPR / CDR by servicer (top 5 by deals held)",
            $"""
            WITH r AS (
                SELECT DISTINCT ON (deal_id) deal_id, loan_count, cpr, cdr, severity
                FROM surveillance.deal_remit
                WHERE period <= @asOf AND deal_id IN (SELECT deal_id FROM {Pos})
                ORDER BY deal_id, period DESC),
            s AS (
                SELECT d.servicer_id, count(*) n, sum(r.cpr * r.loan_count) / NULLIF(sum(r.loan_count), 0) cpr,
                       sum(r.cdr * r.loan_count) / NULLIF(sum(r.loan_count), 0) cdr,
                       sum(r.severity * r.loan_count) / NULLIF(sum(r.loan_count), 0) sev
                FROM r JOIN core.deal d USING (deal_id)
                GROUP BY d.servicer_id)
            SELECT v.name, s.n, s.cpr, s.cdr, s.sev
            FROM s JOIN reference.servicer v USING (servicer_id)
            ORDER BY s.n DESC, s.servicer_id
            LIMIT 5
            """,
            ["Servicer", "Deals", "CPR", "CDR", "Severity"], ["text", "int", "pct1", "pct2", "pct1"]),

        // Held MV per deal is aggregated before it meets the deal's remittance (one row per deal on both sides).
        new(Surveillance, "oc_cushion_buckets", "CLO OC test cushion",
            $"""
            WITH mv AS (SELECT deal_id, sum(market_value) mv FROM {Pos} GROUP BY deal_id),
            r AS (
                SELECT DISTINCT ON (deal_id) deal_id, oc_cushion
                FROM surveillance.deal_remit
                WHERE period <= @asOf AND oc_cushion IS NOT NULL AND deal_id IN (SELECT deal_id FROM mv)
                ORDER BY deal_id, period DESC),
            b AS (
                SELECT CASE WHEN r.oc_cushion < 0 THEN 0 WHEN r.oc_cushion < 0.02 THEN 1 WHEN r.oc_cushion < 0.04 THEN 2
                            WHEN r.oc_cushion < 0.06 THEN 3 ELSE 4 END k, mv.mv
                FROM r JOIN mv USING (deal_id))
            SELECT (ARRAY['< 0% (failing)','0–2%','2–4%','4–6%','6%+'])[k + 1], count(*), sum(mv)
            FROM b GROUP BY k ORDER BY k
            """,
            ["OC cushion", "Deals", "Held MV"], ["text", "int", "money0"]),

        new(Surveillance, "warf_clo_vintage", "CLO WARF by vintage",
            $"""
            WITH r AS (
                SELECT DISTINCT ON (deal_id) deal_id, warf, oc_cushion, ic_cushion
                FROM surveillance.deal_remit
                WHERE period <= @asOf AND warf IS NOT NULL AND deal_id IN (SELECT deal_id FROM {Pos})
                ORDER BY deal_id, period DESC)
            SELECT d.vintage::text, count(*), avg(r.warf), avg(r.oc_cushion), avg(r.ic_cushion)
            FROM r JOIN core.deal d USING (deal_id)
            WHERE d.sector = 'CLO'
            GROUP BY d.vintage
            ORDER BY d.vintage
            """,
            ["Vintage", "Deals", "WARF", "OC cushion", "IC cushion"], ["text", "int", "num0", "pct1", "pct1"]),

        new(Surveillance, "ltv_fico_bands", "Residential deals by LTV × FICO band",
            $"""
            WITH r AS (
                SELECT DISTINCT ON (deal_id) deal_id, ltv, fico
                FROM surveillance.deal_remit
                WHERE period <= @asOf AND deal_id IN (SELECT deal_id FROM {Pos})
                ORDER BY deal_id, period DESC)
            SELECT CASE WHEN r.ltv < 0.6 THEN '< 60%' WHEN r.ltv < 0.7 THEN '60–70%' WHEN r.ltv < 0.8 THEN '70–80%' ELSE '80%+' END,
                   CASE WHEN r.fico < 700 THEN '< 700' WHEN r.fico < 740 THEN '700–739' WHEN r.fico < 780 THEN '740–779' ELSE '780+' END,
                   count(*)
            FROM r JOIN core.deal d USING (deal_id)
            WHERE d.sector IN ('RMBS', 'CRT')
            GROUP BY 1, 2
            ORDER BY min(r.ltv)
            """,
            ["LTV", "< 700", "700–739", "740–779", "780+"], ["text", "int", "int", "int", "int"], ["< 700", "700–739", "740–779", "780+"]),

        // ---------- pricing (3): marks on the as-of date for the bonds held; vendor marks averaged per bond first ----------
        new(Pricing, "vendor_dispersion", "Vendor price dispersion (max − min, pts)",
            $"""
            WITH held AS (SELECT bond_id, sum(market_value) mv FROM {Pos} GROUP BY bond_id),
            v AS (
                SELECT bond_id, max(price) - min(price) spread
                FROM pricing.vendor_mark
                WHERE as_of_date = @asOf AND bond_id IN (SELECT bond_id FROM held)
                GROUP BY bond_id),
            b AS (
                SELECT CASE WHEN v.spread < 0.25 THEN 0 WHEN v.spread < 0.5 THEN 1 WHEN v.spread < 1 THEN 2 WHEN v.spread < 2 THEN 3 ELSE 4 END k, held.mv
                FROM v JOIN held USING (bond_id))
            SELECT (ARRAY['< 0.25','0.25–0.5','0.5–1','1–2','2+'])[k + 1], count(*), sum(mv)
            FROM b GROUP BY k ORDER BY k
            """,
            ["Dispersion", "Bonds", "Held MV"], ["text", "int", "money0"]),

        new(Pricing, "internal_vs_vendor", "Internal vs vendor mark by sector (pts)",
            $"""
            WITH held AS (SELECT bond_id, min(sector) sector FROM {Pos} GROUP BY bond_id),
            v AS (SELECT bond_id, avg(price) price FROM pricing.vendor_mark WHERE as_of_date = @asOf AND bond_id IN (SELECT bond_id FROM held) GROUP BY bond_id),
            x AS (
                SELECT held.sector, i.price - v.price diff
                FROM held JOIN v USING (bond_id) JOIN pricing.internal_mark i ON i.bond_id = held.bond_id AND i.as_of_date = @asOf)
            SELECT sector, count(*), avg(diff), avg(abs(diff)), count(*) FILTER (WHERE abs(diff) > 1)
            FROM x GROUP BY sector
            ORDER BY {BySector("sector")}
            """,
            ["Sector", "Bonds", "Avg diff", "Avg |diff|", "> 1 pt"], ["text", "int", "num2", "num2", "int"]),

        new(Pricing, "challenged_marks", "Challenged marks by sector",
            $"""
            WITH held AS (SELECT bond_id, min(sector) sector, sum(market_value) mv FROM {Pos} GROUP BY bond_id)
            SELECT held.sector, count(*), count(*) FILTER (WHERE i.challenged),
                   count(*) FILTER (WHERE i.challenged)::numeric / count(*),
                   coalesce(sum(held.mv) FILTER (WHERE i.challenged), 0)
            FROM held JOIN pricing.internal_mark i ON i.bond_id = held.bond_id AND i.as_of_date = @asOf
            GROUP BY held.sector
            ORDER BY {BySector("held.sector")}
            """,
            ["Sector", "Bonds", "Challenged", "% challenged", "Challenged MV"], ["text", "int", "int", "pct1", "money0"]),

        // ---------- reference (3) ----------
        new(Reference, "servicer_exposure", "Servicer exposure (top 5 by MV)",
            $"""
            WITH x AS (
                SELECT d.servicer_id, sum(p.market_value) mv, count(DISTINCT p.deal_id) deals
                FROM {PosWithDeal}
                GROUP BY d.servicer_id),
            ranked AS (SELECT servicer_id, mv, deals, mv / NULLIF(sum(mv) OVER (), 0) share FROM x)
            SELECT s.name, r.mv, r.share, r.deals
            FROM ranked r JOIN reference.servicer s USING (servicer_id)
            ORDER BY r.mv DESC NULLS LAST, r.servicer_id
            LIMIT 5
            """,
            ["Servicer", "MV", "% of book", "Deals"], ["text", "money0", "pct1", "int"]),

        new(Reference, "trustee_exposure", "Trustee exposure (top 5 by MV)",
            $"""
            WITH x AS (
                SELECT d.trustee_id, sum(p.market_value) mv, count(DISTINCT p.deal_id) deals
                FROM {PosWithDeal}
                GROUP BY d.trustee_id),
            ranked AS (SELECT trustee_id, mv, deals, mv / NULLIF(sum(mv) OVER (), 0) share FROM x)
            SELECT t.name, r.mv, r.share, r.deals
            FROM ranked r JOIN reference.trustee t USING (trustee_id)
            ORDER BY r.mv DESC NULLS LAST, r.trustee_id
            LIMIT 5
            """,
            ["Trustee", "MV", "% of book", "Deals"], ["text", "money0", "pct1", "int"]),

        // Each agency's rating is mapped to a common rank (reference.rating_scale) and shown on the S&P scale.
        new(Reference, "rating_agency_split", "MV by rating, per agency",
            $"""
            WITH p AS (SELECT * FROM {Pos}),
            r AS (
                SELECT 'S&P' agency, coalesce(rating_sp, 'NR') rating, market_value FROM p
                UNION ALL SELECT 'Moody''s', coalesce(rating_moodys, 'NR'), market_value FROM p
                UNION ALL SELECT 'Fitch', coalesce(rating_fitch, 'NR'), market_value FROM p)
            SELECT sp.rating, r.agency, sum(r.market_value)
            FROM r
            JOIN reference.rating_scale s ON s.agency = r.agency AND s.rating = r.rating
            JOIN reference.rating_scale sp ON sp.agency = 'S&P' AND sp.rank = s.rank
            GROUP BY sp.rank, sp.rating, r.agency
            ORDER BY sp.rank
            """,
            ["Rating", "S&P", "Moody's", "Fitch"], ["text", "money0", "money0", "money0"], ["S&P", "Moody's", "Fitch"]),
    ];

    public static IReadOnlyList<InsightSpec> For(string source) => [.. All.Where(s => s.Source == source)];

    public static InsightSpec? Find(string source, string id) => All.FirstOrDefault(s => s.Source == source && s.Id == id);
}
