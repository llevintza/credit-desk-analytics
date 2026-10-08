using Desk.Data.Catalog;

namespace Desk.Seeder.Generation;

public sealed record Position(long PositionId, Portfolio Portfolio, Bond Bond, Deal Deal, decimal Face, BondAnalytics Analytics, int AccruedDays, int Seq, double BookPrice);

/// <summary>Row generators for every seeded table. Each table draws from its own RNG stream (see <see cref="Rng.For"/>).</summary>
public sealed class Tables(int seed, double scale, DateOnly asOf, Universe u)
{
    public DateOnly AsOf { get; } = asOf;
    public DateOnly PriorBusinessDay { get; } = PreviousBusinessDay(asOf);
    private int Scaled(int n, int min = 1) => Math.Max(min, (int)Math.Round(n * scale));

    public static DateOnly PreviousBusinessDay(DateOnly d)
    {
        do d = d.AddDays(-1); while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        return d;
    }

    public static DateOnly MonthEnd(int year, int month) => new DateOnly(year, month, DateTime.DaysInMonth(year, month));

    public static DateOnly LastBusinessDayOfMonth(int year, int month)
    {
        var d = MonthEnd(year, month);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    private static IEnumerable<DateOnly> BusinessDaysBack(DateOnly end, int count)
    {
        var d = end;
        for (var i = 0; i < count; i++)
        {
            while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
            yield return d;
            d = d.AddDays(-1);
        }
    }

    // ---------- analytics per bond (shared by positions, marks, trades) ----------

    private Dictionary<int, BondAnalytics>? _analytics;
    public IReadOnlyDictionary<int, BondAnalytics> BondAnalytics => _analytics ??= BuildAnalytics();

    private Dictionary<int, BondAnalytics> BuildAnalytics()
    {
        var rng = Rng.For(seed, "analytics");
        var deals = u.Deals.ToDictionary(d => d.DealId);
        var shock = rng.Normal(0, 8); // desk-wide spread move on the as-of date
        return u.Bonds.ToDictionary(b => b.BondId, b => Analytics.For(rng, deals[b.DealId], b, AsOf, shock));
    }

    // ---------- positions ----------

    private List<Position>? _positions;
    public IReadOnlyList<Position> Positions => _positions ??= BuildPositions();

    private List<Position> BuildPositions()
    {
        var rng = Rng.For(seed, "positions");
        var target = Math.Min(Scaled(20_000, 50), u.Bonds.Count * u.Portfolios.Count);
        var totalWeight = u.Portfolios.Sum(p => p.Weight);
        var deals = u.Deals.ToDictionary(d => d.DealId);
        var positions = new List<Position>(target);
        long id = 0;
        var seq = 0;
        foreach (var p in u.Portfolios)
        {
            var count = Math.Min(u.Bonds.Count, (int)Math.Round(target * p.Weight / totalWeight));
            // Partial Fisher–Yates: distinct bonds per portfolio, deterministic order.
            var idx = Enumerable.Range(0, u.Bonds.Count).ToArray();
            for (var i = 0; i < count; i++)
            {
                var j = rng.Int(i, idx.Length - 1);
                (idx[i], idx[j]) = (idx[j], idx[i]);
                var b = u.Bonds[idx[i]];
                // Lognormal sizes around $500k (a ~$10-15B book across 20k lines), in $1k increments.
                var face = Math.Round((decimal)Math.Exp(rng.Normal(Math.Log(500_000), 0.85)) / 1000m, 0) * 1000m;
                face = Math.Clamp(face, 50_000m, 15_000_000m);
                var analytics = BondAnalytics[b.BondId];
                // Cost basis is fixed per position: the same on every as-of date.
                var bookPrice = Math.Round(Math.Max(1, analytics.Price - rng.Normal(0, 2.5)), 4);
                positions.Add(new Position(++id, p, b, deals[b.DealId], face, analytics, rng.Int(1, 30), seq++, bookPrice));
            }
        }
        return positions;
    }

    /// <summary>Wide snapshot rows for one as-of date, values keyed by catalog column name.</summary>
    public IEnumerable<object?[]> PositionSnapshotRows(DateOnly date)
    {
        var prior = date != AsOf;
        var rng = Rng.For(seed, prior ? "snapshot-prior" : "snapshot");
        var cols = ColumnCatalog.PositionSnapshot;
        var index = cols.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i);

        // Portfolio MV totals for pct_of_portfolio_mv (two passes over the same deterministic data).
        var rows = new List<object?[]>(Positions.Count);
        var portfolioMv = new Dictionary<int, decimal>();

        foreach (var pos in Positions)
        {
            var a = pos.Analytics;
            var price = prior ? a.Price - a.PriceChg1d : a.Price;
            var spread = prior ? a.SpreadBp - a.SpreadChg1dBp : a.SpreadBp;
            var b = pos.Bond;
            var currentFace = Math.Round(pos.Face * (decimal)b.Factor, 2);
            var mv = Math.Round(currentFace * (decimal)price / 100m, 2);
            var bookPrice = pos.BookPrice;
            var bookValue = Math.Round(currentFace * (decimal)bookPrice / 100m, 2);
            var accrued = Math.Round(currentFace * (decimal)a.CouponCurrent * pos.AccruedDays / 360m, 2);
            var mvD = (double)mv;
            var dv01 = mvD * a.ModDuration / 10_000;
            var cs01 = mvD * a.SpreadDuration / 10_000;
            portfolioMv[pos.Portfolio.PortfolioId] = portfolioMv.GetValueOrDefault(pos.Portfolio.PortfolioId) + mv;

            var r = new object?[cols.Count];
            void Set(string name, object? value) => r[index[name]] = value;
            decimal M(double v) => Math.Round((decimal)v, 2);

            Set("as_of_date", date); Set("position_id", pos.PositionId); Set("portfolio_id", pos.Portfolio.PortfolioId);
            Set("fund_id", pos.Portfolio.FundId); Set("bond_id", b.BondId); Set("deal_id", pos.Deal.DealId);
            Set("cusip", b.Cusip); Set("deal_name", pos.Deal.Name); Set("class", b.Class); Set("sector", pos.Deal.Sector);
            Set("sub_sector", pos.Deal.SubSector); Set("vintage", pos.Deal.Vintage); Set("rating_composite", b.RatingComposite);
            Set("currency", "USD");

            Set("face", pos.Face); Set("current_face", currentFace); Set("factor", b.Factor);
            Set("book_price", bookPrice); Set("book_value", bookValue); Set("market_value", mv); Set("accrued", accrued);
            Set("unrealized_pnl", mv - bookValue); Set("pct_of_portfolio_mv", null); // filled below

            Set("price", Math.Round(price, 4)); Set("price_chg_1d", a.PriceChg1d); Set("price_chg_1w", a.PriceChg1w);
            Set("price_chg_1m", a.PriceChg1m); Set("yield", a.Yield); Set("spread_bp", Math.Round(spread, 1)); Set("oas_bp", a.OasBp);
            Set("dm_bp", a.DmBp); Set("spread_chg_1d_bp", a.SpreadChg1dBp); Set("spread_chg_1w_bp", a.SpreadChg1wBp);
            Set("spread_chg_1m_bp", a.SpreadChg1mBp); Set("z_spread_bp", a.ZSpreadBp); Set("price_source", a.PriceSource);
            Set("vendor_dispersion_bp", a.VendorDispersionBp);

            Set("mod_duration", a.ModDuration); Set("eff_duration", a.EffDuration); Set("convexity", a.Convexity); Set("dv01", M(dv01));
            var walBucket = a.Wal < 3 ? 0 : a.Wal < 7 ? 1 : a.Wal < 15 ? 2 : 3;
            double[][] krdWeights = [[0.7, 0.3, 0, 0, 0], [0.2, 0.6, 0.2, 0, 0], [0.05, 0.25, 0.55, 0.15, 0], [0, 0.1, 0.3, 0.35, 0.25]];
            var tenors = new[] { 2, 5, 10, 20, 30 };
            for (var t = 0; t < tenors.Length; t++) Set($"krd_{tenors[t]}y", M(dv01 * krdWeights[walBucket][t]));

            Set("rating_sp", b.RatingSp); Set("rating_moodys", b.RatingMoodys); Set("rating_fitch", b.RatingFitch);
            Set("spread_duration", a.SpreadDuration); Set("cs01", M(cs01)); Set("jtd", M(-mvD * a.Severity12m));
            Set("expected_loss_pct", a.ExpectedLossPct); Set("attachment_pct", b.AttachmentPct); Set("detachment_pct", b.DetachmentPct);
            Set("credit_enhancement_pct", a.CreditEnhancementPct);

            Set("wal", a.Wal); Set("window_start", date.AddDays((int)(a.Wal * 365 * 0.6)));
            Set("window_end", date.AddDays((int)(a.Wal * 365 * 1.4)));
            var nextPay = new DateOnly(date.Year, date.Month, 25); if (nextPay <= date) nextPay = nextPay.AddMonths(1);
            Set("next_pay_date", nextPay); Set("coupon_current", a.CouponCurrent); Set("coupon_next", a.CouponNext);

            Set("cpr_1m", a.Cpr1m); Set("cpr_3m", a.Cpr3m); Set("cpr_12m", a.Cpr12m); Set("cdr_1m", a.Cdr1m); Set("cdr_3m", a.Cdr3m);
            Set("cdr_12m", a.Cdr12m); Set("severity_3m", a.Severity3m); Set("severity_12m", a.Severity12m); Set("dq_30", a.Dq30);
            Set("dq_60", a.Dq60); Set("dq_90plus", a.Dq90Plus); Set("foreclosure_pct", a.ForeclosurePct); Set("reo_pct", a.ReoPct);
            Set("wac", a.Wac); Set("wala", a.Wala); Set("ltv_wavg", a.Ltv); Set("fico_wavg", a.Fico); Set("loan_count", a.LoanCount);

            Set("oc_test_cushion", a.OcCushion); Set("ic_test_cushion", a.IcCushion); Set("warf", a.Warf);
            Set("diversity_score", a.Diversity); Set("ccc_bucket_pct", a.CccBucket);

            var carry = mvD * a.CouponCurrent / 12;
            var rates = -dv01 * rng.Normal(0, 12);
            var spreadPnl = -cs01 * a.SpreadChg1mBp;
            var roll = mvD * rng.Uniform(0, 0.0008);
            var idio = mvD * rng.Normal(0, 0.002);
            var residual = mvD * rng.Normal(0, 0.0003);
            Set("pnl_carry", M(carry)); Set("pnl_roll_down", M(roll)); Set("pnl_rates", M(rates)); Set("pnl_spread", M(spreadPnl));
            Set("pnl_idiosyncratic", M(idio)); Set("pnl_fx", 0m); Set("pnl_residual", M(residual));
            Set("pnl_total_mtd", M(carry) + M(roll) + M(rates) + M(spreadPnl) + M(idio) + M(residual));

            var worst = double.MaxValue; var best = 0.0;
            var aForDate = prior ? a with { Price = price } : a;
            foreach (var (rate, spr) in Analytics.Scenarios())
            {
                var sp = Analytics.ScenarioPrice(aForDate, rate, spr);
                Set(ColumnCatalog.ScenarioColumn(rate, spr), sp);
                worst = Math.Min(worst, sp); best = Math.Max(best, sp);
            }
            Set("worst_case_price", worst); Set("best_case_price", best); Set("scenario_range", Math.Round(best - worst, 4));
            Set("stress_loss_mv", Math.Round(currentFace * (decimal)(worst - price) / 100m, 2));

            Set("watchlist_flag", rng.Chance(0.03 + b.RatingRank * 0.01)); Set("restricted_flag", rng.Chance(0.01));
            Set("comment_count", rng.Chance(0.8) ? 0 : rng.Int(1, 9));
            Set("last_trade_date", date.AddDays(-rng.Int(0, 400))); Set("analyst", $"analyst{(pos.Deal.DealId % 8) + 1:00}");
            rows.Add(r);
        }

        var pctIdx = index["pct_of_portfolio_mv"];
        var mvIdx = index["market_value"];
        var portIdx = index["portfolio_id"];
        foreach (var r in rows)
        {
            var total = portfolioMv[(int)r[portIdx]!];
            r[pctIdx] = total == 0 ? null : Math.Round((double)((decimal)r[mvIdx]! / total), 6);
        }
        return rows;
    }

    /// <summary>Narrow month-end history (about 24 month-ends × positions): the "old data traders use".</summary>
    public IEnumerable<object?[]> PositionHistoryRows()
    {
        var rng = Rng.For(seed, "history");
        var lastMonthEnd = MonthEnd(AsOf.Year, AsOf.Month) <= AsOf ? MonthEnd(AsOf.Year, AsOf.Month) : MonthEnd(AsOf.AddMonths(-1).Year, AsOf.AddMonths(-1).Month);
        var months = Enumerable.Range(0, 24).Select(i => lastMonthEnd.AddMonths(-i)).Select(d => MonthEnd(d.Year, d.Month)).ToList();
        foreach (var pos in Positions)
        {
            var a = pos.Analytics;
            var price = a.Price;
            var spread = a.SpreadBp;
            var factor = pos.Bond.Factor;
            for (var m = 0; m < months.Count; m++)
            {
                var face = Math.Round(pos.Face * (decimal)factor, 2);
                var mv = Math.Round(face * (decimal)price / 100m, 2);
                var pnl = Math.Round(mv * (decimal)rng.Normal(0.003, 0.012), 2);
                yield return [months[m], pos.PositionId, mv, face, Math.Round(price, 4), Math.Round(spread, 1),
                    Math.Round(mv * (decimal)a.ModDuration / 10_000m, 2), Math.Round(mv * (decimal)a.SpreadDuration / 10_000m, 2),
                    Math.Round(a.Wal + m / 12.0, 2), pnl];
                // Walk backwards in time: older months had higher factors and slightly different prices.
                price = Math.Clamp(price + rng.Normal(0, 0.7), 20, 110);
                spread = Math.Max(10, spread + rng.Normal(0, 6));
                factor = Math.Min(1.0, factor * rng.Uniform(1.0, 1.02));
            }
        }
    }

    public IEnumerable<object?[]> TradeRows()
    {
        var rng = Rng.For(seed, "trades");
        var count = Scaled(150_000, 100);
        var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var days = BusinessDaysBack(AsOf, 520).ToList();
        for (long id = 1; id <= count; id++)
        {
            var pos = Positions[rng.Int(0, Positions.Count - 1)];
            var day = days[rng.Int(0, days.Count - 1)];
            DateTime local;
            if (id % 997 == 0)
            {
                // README §5.2 edge case: month-end trades booked at 23:59:59.xxx, on the month's last business
                // day that is on or before the as-of date (never in the future, never on a weekend).
                var me = LastBusinessDayOfMonth(day.Year, day.Month);
                if (me > AsOf) me = LastBusinessDayOfMonth(day.AddMonths(-1).Year, day.AddMonths(-1).Month);
                local = me.ToDateTime(new TimeOnly(23, 59, 59)).AddMilliseconds(rng.Int(0, 999));
            }
            else
            {
                local = day.ToDateTime(new TimeOnly(8, 0)).AddMinutes(rng.Int(0, 570)).AddSeconds(rng.Int(0, 59));
            }
            var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), ny), TimeSpan.Zero);
            var face = Math.Round((decimal)rng.Uniform(0.1, 5) * 1_000_000m / 1000m, 0) * 1000m;
            yield return [id, pos.Bond.BondId, pos.Portfolio.PortfolioId, utc, rng.Chance(0.5) ? "B" : "S", face,
                Math.Round(pos.Analytics.Price + rng.Normal(0, 0.8), 4), rng.Int(1, 40), $"trader{rng.Int(1, 12):00}"];
        }
    }

    // ---------- funds ----------

    public IEnumerable<object?[]> FundPerformanceRows()
    {
        var rng = Rng.For(seed, "fund-performance");
        var lastMonthEnd = MonthEnd(AsOf.AddMonths(-1).Year, AsOf.AddMonths(-1).Month);
        foreach (var f in u.Funds)
        {
            var first = MonthEnd(f.InceptionDate.Year, f.InceptionDate.Month); // mid-month inception -> that month's end
            var nav = Math.Round((decimal)rng.Uniform(1_500, 4_000) * 1_000_000m, 2); // sized to the ~$10-15B position book
            var leverage = rng.Uniform(1.2, 1.6);
            var growth = 1.0;
            var ytd = 1.0;
            var n = 0;
            for (var m = first; m <= lastMonthEnd; m = MonthEnd(m.AddMonths(1).Year, m.AddMonths(1).Month))
            {
                var ret = rng.Normal(0.0075, 0.014);
                var flows = Math.Round((decimal)rng.Normal(0, 40_000_000), 2);
                if (m.Month == 1) ytd = 1.0;
                if (n > 0)
                {
                    growth *= 1 + ret; ytd *= 1 + ret;
                    nav = Math.Round(nav * (decimal)(1 + ret) + flows, 2);
                }
                n++;
                var irrItd = n < 2 ? 0 : Math.Pow(growth, 12.0 / (n - 1)) - 1;
                yield return [f.FundId, m, nav, Math.Round(nav * (decimal)leverage, 2), Math.Round(irrItd, 6), Math.Round(ytd - 1, 6), n == 1 ? 0m : flows];
            }
        }
    }

    public IEnumerable<object?[]> FundFlowRows()
    {
        var rng = Rng.For(seed, "fund-flow");
        long id = 0;
        foreach (var f in u.Funds)
        {
            for (var d = f.InceptionDate; d <= AsOf; d = d.AddDays(rng.Int(8, 20)))
            {
                yield return [++id, f.FundId, d, Math.Round((decimal)rng.Normal(0, 25_000_000), 2)];
                // README §5.2 edge case: two flows on the same date (window-frame tie tests).
                if (rng.Chance(0.15)) yield return [++id, f.FundId, d, Math.Round((decimal)rng.Normal(0, 10_000_000), 2)];
            }
        }
    }

    // ---------- market ----------

    public IEnumerable<object?[]> RateCurveRows()
    {
        var rng = Rng.For(seed, "rates");
        int[] tenors = [1, 3, 6, 12, 24, 36, 60, 84, 120, 240, 360];
        var days = BusinessDaysBack(AsOf, Scaled(520, 30)).Reverse().ToList();
        var level = 0.043; var slope = 0.002;
        foreach (var d in days)
        {
            level = Math.Clamp(level + rng.Normal(0, 0.0004), 0.005, 0.08);
            slope = Math.Clamp(slope + rng.Normal(0, 0.0002), -0.015, 0.02);
            foreach (var curve in new[] { "UST", "SOFR" })
                foreach (var t in tenors)
                    yield return [d, curve, t, Math.Round(level + slope * Math.Log(1 + t / 12.0) + (curve == "SOFR" ? -0.001 : 0), 6)];
        }
    }

    public IEnumerable<object?[]> SpreadIndexRows()
    {
        var rng = Rng.For(seed, "spread-index");
        var days = BusinessDaysBack(AsOf, Scaled(520, 30)).Reverse().ToList();
        string[] sectors = ["CLO", "RMBS", "CMBS", "ABS", "CRT"];
        string[] ratings = ["AAA", "AA", "A", "BBB", "BB", "B", "CCC"];
        var state = sectors.SelectMany(s => ratings.Select((r, i) => (s, r, v: 60 * Math.Pow(1.75, i)))).ToDictionary(x => (x.s, x.r), x => x.v);
        foreach (var d in days)
            foreach (var key in state.Keys.ToList())
            {
                state[key] = Math.Max(10, state[key] * Math.Exp(rng.Normal(0, 0.01)));
                yield return [d, key.s, key.r, Math.Round(state[key], 1)];
            }
    }

    // ---------- surveillance ----------

    public IEnumerable<object?[]> DealRemitRows()
    {
        var rng = Rng.For(seed, "remit");
        var analyticsByDeal = u.Bonds.GroupBy(b => b.DealId).ToDictionary(g => g.Key, g => BondAnalytics[g.First().BondId]);
        foreach (var deal in u.Deals)
        {
            if (!analyticsByDeal.TryGetValue(deal.DealId, out var a)) continue; // no bonds yet -> no remittance
            for (var m = 0; m < 24; m++)
            {
                var period = MonthEnd(AsOf.AddMonths(-m - 1).Year, AsOf.AddMonths(-m - 1).Month);
                if (period < deal.ClosingDate) break;
                var drift = 1 + m * 0.01;
                double J(double v) => Math.Round(Math.Max(0, v * drift * Math.Exp(rng.Normal(0, 0.12))), 4);
                yield return [deal.DealId, period, Math.Max(1, (int)(a.LoanCount * (1 + m * 0.01))), J(a.Dq30), J(a.Dq60), J(a.Dq90Plus),
                    J(a.Cpr1m), J(a.Cdr1m), Math.Round(a.Severity3m, 4), a.Wac, Math.Max(1, a.Wala - m), a.Ltv, a.Fico,
                    a.OcCushion, a.IcCushion, a.Warf];
            }
        }
    }

    // ---------- pricing ----------

    public IEnumerable<object?[]> VendorMarkRows()
    {
        var rng = Rng.For(seed, "vendor-marks");
        var days = BusinessDaysBack(AsOf, Scaled(22, 3)).ToList();
        foreach (var b in u.Bonds)
        {
            var a = BondAnalytics[b.BondId];
            for (var i = 0; i < days.Count; i++)
            {
                var basePrice = a.Price - a.PriceChg1d * i * 0.3;
                foreach (var vendor in new[] { "Vendor A", "Vendor B", "Vendor C" })
                    yield return [days[i], b.BondId, vendor, Math.Round(basePrice + rng.Normal(0, a.VendorDispersionBp / 100 * a.SpreadDuration), 4)];
            }
        }
    }

    public IEnumerable<object?[]> InternalMarkRows()
    {
        var rng = Rng.For(seed, "internal-marks");
        var days = BusinessDaysBack(AsOf, Scaled(22, 3)).ToList();
        foreach (var b in u.Bonds)
        {
            var a = BondAnalytics[b.BondId];
            for (var i = 0; i < days.Count; i++)
            {
                var off = rng.Normal(0, 0.35);
                yield return [days[i], b.BondId, Math.Round(a.Price - a.PriceChg1d * i * 0.3 + off, 4), Math.Abs(off) > 0.75];
            }
        }
    }
}
