using Desk.Data.Catalog;

namespace Desk.Seeder.Generation;

/// <summary>Bond-level analytics for one as-of date; positions scale the money measures by their holding.</summary>
public sealed record BondAnalytics(
    double Price, double Yield, double SpreadBp, double OasBp, double DmBp, double ZSpreadBp,
    double PriceChg1d, double PriceChg1w, double PriceChg1m, double SpreadChg1dBp, double SpreadChg1wBp, double SpreadChg1mBp,
    double ModDuration, double EffDuration, double Convexity, double SpreadDuration, double Wal,
    double CouponCurrent, double CouponNext, double ExpectedLossPct, double CreditEnhancementPct,
    double Cpr1m, double Cpr3m, double Cpr12m, double Cdr1m, double Cdr3m, double Cdr12m, double Severity3m, double Severity12m,
    double Dq30, double Dq60, double Dq90Plus, double ForeclosurePct, double ReoPct, double Wac, double Wala,
    double Ltv, double Fico, int LoanCount, double? OcCushion, double? IcCushion, double? Warf, double? Diversity, double? CccBucket,
    double VendorDispersionBp, string PriceSource);

public static class Analytics
{
    private static readonly Dictionary<string, double> SectorSpreadBp = new()
    {
        ["CLO"] = 140, ["RMBS"] = 160, ["CMBS"] = 190, ["ABS"] = 95, ["CRT"] = 210,
    };

    /// <summary>
    /// Correlated draws: spread ~ rating × sector × vintage; price ~ spread vs coupon × duration;
    /// collateral performance ~ sector × vintage. Values are clamped to plausible desk ranges.
    /// </summary>
    public static BondAnalytics For(Rng rng, Deal deal, Bond bond, DateOnly asOf, double marketShockBp)
    {
        var equity = bond.CouponType == "RESIDUAL";
        var ratingMult = Math.Pow(1.75, bond.RatingRank);
        var vintageMult = deal.Vintage <= 2007 ? 1.6 : deal.Vintage >= 2022 ? 1.15 : 1.0;
        var spread = Math.Max(15, SectorSpreadBp[deal.Sector] * 0.55 * ratingMult * vintageMult * Math.Exp(rng.Normal(0, 0.18)) + marketShockBp);
        if (equity) spread = rng.Uniform(900, 1800);

        var wal = Math.Clamp(bond.ExpectedMaturity.DayNumber - asOf.DayNumber, 120, 15 * 365) / 365.25 * rng.Uniform(0.55, 0.85);
        var floating = bond.CouponType == "FLOAT";
        var modDur = floating ? rng.Uniform(0.05, 0.35) : Math.Clamp(wal * rng.Uniform(0.78, 0.92), 0.2, 12);
        var spreadDur = Math.Clamp(wal * rng.Uniform(0.80, 0.95), 0.2, 12);
        var convexity = floating ? rng.Uniform(-0.05, 0.05) : rng.Uniform(-0.8, 0.6) * modDur / 4;

        var sofr = 0.043;
        var coupon = bond.CouponOrMargin is { } c ? (floating ? sofr + c : c) : 0.0;
        var price = equity
            ? rng.Uniform(25, 85)
            : Math.Clamp(100 + (coupon * 10_000 - (floating ? sofr * 10_000 : 450) - spread) / 100 * spreadDur * 0.9 + rng.Normal(0, 0.4), 35, 104);
        var yield = equity ? rng.Uniform(0.14, 0.24) : sofr + spread / 10_000 + (floating ? 0 : rng.Uniform(-0.002, 0.002));

        var chg1m = rng.Normal(0, equity ? 3.0 : 0.6 * (1 + bond.RatingRank * 0.4));
        var chg1w = chg1m * rng.Uniform(0.2, 0.6) + rng.Normal(0, 0.1);
        var chg1d = chg1w * rng.Uniform(0.1, 0.4) + rng.Normal(0, 0.05);
        double SpreadFromPrice(double p) => -p / Math.Max(spreadDur, 0.2) * 100;

        var sectorCpr = deal.Sector switch { "RMBS" => 0.09, "ABS" => 0.18, "CMBS" => 0.02, "CRT" => 0.10, _ => 0.20 };
        var sectorCdr = deal.Sector switch { "RMBS" => 0.012, "ABS" => 0.025, "CMBS" => 0.015, "CRT" => 0.006, _ => 0.02 } * vintageMult;
        double Jitter(double v, double sd) => Math.Max(0, v * Math.Exp(rng.Normal(0, sd)));
        var dq60 = Jitter(sectorCdr * 1.6, 0.35);
        var isClo = deal.Sector == "CLO";

        return new BondAnalytics(
            Price: Math.Round(price, 4), Yield: Math.Round(yield, 6), SpreadBp: Math.Round(spread, 1),
            OasBp: Math.Round(spread - rng.Uniform(5, 25), 1), DmBp: floating ? Math.Round(spread + rng.Uniform(-8, 8), 1) : Math.Round(spread, 1),
            ZSpreadBp: Math.Round(spread + rng.Uniform(-4, 12), 1),
            PriceChg1d: Math.Round(chg1d, 4), PriceChg1w: Math.Round(chg1w, 4), PriceChg1m: Math.Round(chg1m, 4),
            SpreadChg1dBp: Math.Round(SpreadFromPrice(chg1d), 1), SpreadChg1wBp: Math.Round(SpreadFromPrice(chg1w), 1),
            SpreadChg1mBp: Math.Round(SpreadFromPrice(chg1m), 1),
            ModDuration: Math.Round(modDur, 3), EffDuration: Math.Round(modDur * rng.Uniform(0.92, 1.05), 3),
            Convexity: Math.Round(convexity, 3), SpreadDuration: Math.Round(spreadDur, 3), Wal: Math.Round(wal, 2),
            CouponCurrent: Math.Round(coupon, 6), CouponNext: Math.Round(floating ? coupon + rng.Normal(0, 0.0004) : coupon, 6),
            ExpectedLossPct: Math.Round(Math.Clamp(bond.RatingRank * bond.RatingRank * 0.004 * vintageMult * rng.Uniform(0.6, 1.4), 0, 0.9), 4),
            CreditEnhancementPct: Math.Round(bond.AttachmentPct, 4),
            Cpr1m: Math.Round(Jitter(sectorCpr, 0.4), 4), Cpr3m: Math.Round(Jitter(sectorCpr, 0.25), 4), Cpr12m: Math.Round(Jitter(sectorCpr, 0.15), 4),
            Cdr1m: Math.Round(Jitter(sectorCdr, 0.5), 4), Cdr3m: Math.Round(Jitter(sectorCdr, 0.3), 4), Cdr12m: Math.Round(Jitter(sectorCdr, 0.2), 4),
            Severity3m: Math.Round(Math.Clamp(rng.Normal(0.35, 0.08), 0.05, 0.9), 4), Severity12m: Math.Round(Math.Clamp(rng.Normal(0.38, 0.06), 0.05, 0.9), 4),
            Dq30: Math.Round(Jitter(sectorCdr * 2.5, 0.35), 4), Dq60: Math.Round(dq60, 4), Dq90Plus: Math.Round(Jitter(dq60 * 1.4, 0.35), 4),
            ForeclosurePct: Math.Round(Jitter(dq60 * 0.5, 0.4), 4), ReoPct: Math.Round(Jitter(dq60 * 0.15, 0.5), 4),
            Wac: Math.Round(isClo ? sofr + rng.Uniform(0.030, 0.045) : rng.Uniform(0.045, 0.085), 4),
            Wala: Math.Round(Math.Max(1, (asOf.DayNumber - deal.ClosingDate.DayNumber) / 30.44 + rng.Uniform(3, 18)), 1),
            Ltv: Math.Round(Math.Clamp(rng.Normal(deal.Sector == "CMBS" ? 0.58 : 0.71, 0.07), 0.3, 1.05), 4),
            Fico: Math.Round(Math.Clamp(rng.Normal(deal.SubSector == "Non-QM" ? 715 : 752, 18), 600, 820), 0),
            LoanCount: isClo ? rng.Int(180, 450) : deal.Sector == "CMBS" ? rng.Int(1, 90) : rng.Int(800, 25_000),
            OcCushion: isClo ? Math.Round(rng.Normal(0.045, 0.02) - bond.RatingRank * 0.004, 4) : null,
            IcCushion: isClo ? Math.Round(rng.Normal(0.12, 0.05), 4) : null,
            Warf: isClo ? Math.Round(rng.Normal(2950, 220), 0) : null,
            Diversity: isClo ? Math.Round(rng.Normal(78, 9), 0) : null,
            CccBucket: isClo ? Math.Round(Math.Clamp(rng.Normal(0.055, 0.02), 0, 0.2), 4) : null,
            VendorDispersionBp: Math.Round(Math.Abs(rng.Normal(0, 6 + bond.RatingRank * 5)), 1),
            PriceSource: rng.Pick(new[] { ("Vendor A", 0.4), ("Vendor B", 0.3), ("Vendor C", 0.2), ("Internal", 0.1) }));
    }

    /// <summary>Price under a rate and spread shock (bp): duration + convexity + spread duration, floored at 0.</summary>
    public static double ScenarioPrice(BondAnalytics a, int rateBp, int spreadBp)
    {
        var dr = rateBp / 10_000.0;
        var ds = spreadBp / 10_000.0;
        var pct = -a.ModDuration * dr + 0.5 * a.Convexity * dr * dr * 100 - a.SpreadDuration * ds;
        return Math.Round(Math.Max(0, a.Price * (1 + pct)), 4);
    }

    public static IEnumerable<(int Rate, int Spread)> Scenarios() =>
        from r in ColumnCatalog.ScenarioRateShocksBp from s in ColumnCatalog.ScenarioSpreadShocksBp select (r, s);
}
