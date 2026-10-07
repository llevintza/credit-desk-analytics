using System.Diagnostics;
using Desk.Data.Catalog;
using Desk.Seeder.Generation;
using Npgsql;
using NpgsqlTypes;

namespace Desk.Seeder;

public sealed record LoadStat(string Table, long Rows, TimeSpan Elapsed);

/// <summary>Bulk loads generated rows with Npgsql binary COPY (README §5.5, ADR-0004).</summary>
public static class Loader
{
    /// <summary>Every table the seeder owns, children first (TRUNCATE order). Accounts are never touched.</summary>
    public static readonly string[] SeededTables =
    [
        "pricing.internal_mark", "pricing.vendor_mark", "surveillance.deal_remit", "market.spread_index", "market.rate_curve",
        "core.fund_flow", "core.fund_performance", "core.trade", "core.position_history", "core.position_snapshot",
        "core.bond", "core.deal", "core.portfolio", "core.fund",
        "reference.sector", "reference.rating_scale", "reference.trustee", "reference.servicer", "reference.issuer",
        "app.column_catalog",
    ];

    public static long Copy(NpgsqlConnection conn, string table, string[] columns, NpgsqlDbType[] types, IEnumerable<object?[]> rows)
    {
        using var w = conn.BeginBinaryImport($"COPY {table} ({string.Join(", ", columns)}) FROM STDIN (FORMAT BINARY)");
        long n = 0;
        foreach (var r in rows)
        {
            w.StartRow();
            for (var i = 0; i < columns.Length; i++)
            {
                if (r[i] is null) w.WriteNull();
                else w.Write(r[i], types[i]);
            }
            n++;
        }
        w.Complete();
        return n;
    }

    public static NpgsqlDbType DbType(ColumnDef c) => c.Kind switch
    {
        ColumnKind.Key => c.Name == "position_id" ? NpgsqlDbType.Bigint : NpgsqlDbType.Integer,
        ColumnKind.Text => NpgsqlDbType.Text,
        ColumnKind.Date => NpgsqlDbType.Date,
        ColumnKind.Money => NpgsqlDbType.Numeric,
        ColumnKind.Count => NpgsqlDbType.Integer,
        ColumnKind.Flag => NpgsqlDbType.Boolean,
        _ => NpgsqlDbType.Double,
    };

    /// <summary>Truncates and reloads every seeded table inside the caller's transaction.</summary>
    public static List<LoadStat> LoadAll(NpgsqlConnection conn, Universe u, Tables t)
    {
        var stats = new List<LoadStat>();
        void Load(string table, string cols, NpgsqlDbType[] types, Func<IEnumerable<object?[]>> rows)
        {
            var sw = Stopwatch.StartNew();
            var n = Copy(conn, table, cols.Split(',', StringSplitOptions.TrimEntries), types, rows());
            stats.Add(new LoadStat(table, n, sw.Elapsed));
        }
        const NpgsqlDbType I = NpgsqlDbType.Integer, L = NpgsqlDbType.Bigint, T = NpgsqlDbType.Text, D = NpgsqlDbType.Date,
            N = NpgsqlDbType.Numeric, F = NpgsqlDbType.Double, B = NpgsqlDbType.Boolean, C = NpgsqlDbType.Char, TS = NpgsqlDbType.TimestampTz;

        using (var cmd = new NpgsqlCommand($"TRUNCATE {string.Join(", ", SeededTables)}", conn)) cmd.ExecuteNonQuery();

        Load("reference.issuer", "issuer_id, name, country", [I, T, T], () => u.Issuers.Select(x => new object?[] { x.Id, x.Name, x.Country }));
        Load("reference.servicer", "servicer_id, name", [I, T], () => u.Servicers.Select(x => new object?[] { x.Id, x.Name }));
        Load("reference.trustee", "trustee_id, name", [I, T], () => u.Trustees.Select(x => new object?[] { x.Id, x.Name }));
        Load("reference.rating_scale", "agency, rating, rank", [T, T, I], () => Universe.Ratings.SelectMany((r, i) => new[]
            { new object?[] { "S&P", r.Sp, i }, new object?[] { "Moody's", r.Moodys, i }, new object?[] { "Fitch", r.Fitch, i } }));
        Load("reference.sector", "sector, sub_sector", [T, T], () => Universe.Sectors.Select(s => new object?[] { s.Sector, s.SubSector }));

        Load("core.fund", "fund_id, name, inception_date, strategy", [I, T, D, T],
            () => u.Funds.Select(f => new object?[] { f.FundId, f.Name, f.InceptionDate, f.Strategy }));
        Load("core.portfolio", "portfolio_id, fund_id, name, manager, benchmark", [I, I, T, T, T],
            () => u.Portfolios.Select(p => new object?[] { p.PortfolioId, p.FundId, p.Name, p.Manager, p.Benchmark }));
        Load("core.deal", "deal_id, name, sector, sub_sector, issuer_id, servicer_id, trustee_id, vintage, closing_date, collateral_type, original_balance, currency, status",
            [I, T, T, T, I, I, I, I, D, T, N, T, T],
            () => u.Deals.Select(d => new object?[] { d.DealId, d.Name, d.Sector, d.SubSector, d.IssuerId, d.ServicerId, d.TrusteeId,
                d.Vintage, d.ClosingDate, d.CollateralType, d.OriginalBalance, "USD", d.Status }));
        Load("core.bond", "bond_id, deal_id, cusip, class, seniority_rank, original_balance, current_balance, factor, coupon_type, coupon_or_margin, coupon_index, rating_sp, rating_moodys, rating_fitch, legal_final, expected_maturity",
            [I, I, C, T, I, N, N, F, T, F, T, T, T, T, D, D],
            () => u.Bonds.Select(b => new object?[] { b.BondId, b.DealId, b.Cusip, b.Class, b.SeniorityRank, b.OriginalBalance,
                b.CurrentBalance, b.Factor, b.CouponType, b.CouponOrMargin, b.CouponIndex, b.RatingSp, b.RatingMoodys, b.RatingFitch,
                b.LegalFinal, b.ExpectedMaturity }));

        var snapCols = ColumnCatalog.PositionSnapshot;
        var snapNames = string.Join(", ", snapCols.Select(c => c.Name));
        var snapTypes = snapCols.Select(DbType).ToArray();
        Load("core.position_snapshot", snapNames, snapTypes,
            () => t.PositionSnapshotRows(t.AsOf).Concat(t.PositionSnapshotRows(t.PriorBusinessDay)));
        Load("core.position_history", "as_of_date, position_id, market_value, face, price, spread_bp, dv01, cs01, wal, pnl_mtd",
            [D, L, N, N, F, F, N, N, F, N], t.PositionHistoryRows);
        Load("core.trade", "trade_id, bond_id, portfolio_id, trade_ts, side, face, price, counterparty_id, trader",
            [L, I, I, TS, C, N, F, I, T], t.TradeRows);
        Load("core.fund_performance", "fund_id, as_of_month, nav, balance, irr_itd, irr_ytd, net_flows", [I, D, N, N, F, F, N], t.FundPerformanceRows);
        Load("core.fund_flow", "flow_id, fund_id, flow_date, amount", [L, I, D, N], t.FundFlowRows);

        Load("market.rate_curve", "as_of_date, curve, tenor_months, rate", [D, T, I, F], t.RateCurveRows);
        Load("market.spread_index", "as_of_date, sector, rating, spread_bp", [D, T, T, F], t.SpreadIndexRows);
        Load("surveillance.deal_remit", "deal_id, period, loan_count, dq_30, dq_60, dq_90plus, cpr, cdr, severity, wac, wala, ltv, fico, oc_cushion, ic_cushion, warf",
            [I, D, I, F, F, F, F, F, F, F, F, F, F, F, F, F], t.DealRemitRows);
        Load("pricing.vendor_mark", "as_of_date, bond_id, vendor, price", [D, I, T, F], t.VendorMarkRows);
        Load("pricing.internal_mark", "as_of_date, bond_id, price, challenged", [D, I, F, B], t.InternalMarkRows);

        Load("app.column_catalog", "name, ordinal, group_name, kind, aggregation, header", [T, I, T, T, T, T],
            () => snapCols.Select((c, i) => new object?[] { c.Name, i, c.Group, c.Kind.ToString(), c.Aggregation.ToString(), c.Header }));
        return stats;
    }
}
