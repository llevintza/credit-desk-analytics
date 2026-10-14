using Dapper;
using Desk.Data.Sources;

namespace Desk.Data.Deals;

/// <summary>
/// One row of the P4 deal list (README §6 P4): the deal, its bond totals, its representative bond (the most senior,
/// lowest bond id; all null for a deal with no bonds yet) and what the caller's portfolios hold of it (all null when
/// none of them does).
/// </summary>
public sealed record DealRow(
    int DealId, string Name, string Sector, string SubSector, int Vintage, string Status, decimal OriginalBalance,
    int BondCount, decimal? CurrentBalance,
    int? RepBondId, string? RepClass, string? RepCusip, decimal? RepCurrentBalance, string? RepCouponType, double? RepCoupon, string? RepRating,
    int? Positions, decimal? HeldFace, decimal? HeldMarketValue);

public sealed record DealList(DateOnly AsOf, DealRow[] Deals);

public sealed record DealInfo(
    int DealId, string Name, string Sector, string SubSector, int Vintage, DateOnly ClosingDate, string CollateralType,
    decimal OriginalBalance, string Currency, string Status);

/// <summary>One tranche of the capital structure, most senior first.</summary>
public sealed record Tranche(
    int BondId, string Class, string Cusip, int SeniorityRank, decimal OriginalBalance, decimal CurrentBalance, double Factor,
    string CouponType, double? CouponOrMargin, string? CouponIndex, string? RatingSp, string? RatingMoodys, string? RatingFitch,
    DateOnly LegalFinal, DateOnly ExpectedMaturity);

/// <summary>One month of the deal's remittance aggregates (surveillance source).</summary>
public sealed record RemitMonth(
    DateOnly Period, int LoanCount, double Dq30, double Dq60, double Dq90Plus, double Cpr, double Cdr, double Severity,
    double? OcCushion, double? IcCushion);

/// <summary>One position the caller's portfolios hold in the deal.</summary>
public sealed record DealPosition(
    long PositionId, int PortfolioId, string Portfolio, int BondId, string? Class, decimal? Face, decimal? MarketValue, double? Price);

public sealed record DealDetail(DateOnly AsOf, DealInfo Deal, Tranche[] Tranches, RemitMonth[] Surveillance, DealPosition[] Positions);

/// <summary>
/// P4 reads (README §6 P4). Deal and bond terms are desk-wide reference data, so every deal is listed, including the
/// ones with no bonds; every portfolio-owned number (positions, held face and MV) is scoped in SQL to the caller's
/// portfolios (ADR-0021). Child rows are aggregated per deal <b>before</b> they meet the deal row, so nothing is
/// counted once per tranche or per position (README §11 grain test).
/// </summary>
public sealed class DealRepository(IDataSourceRegistry sources)
{
    /// <summary>Months of surveillance history in the detail (README §6 P4: a 12-month trend).</summary>
    public const int TrendMonths = 12;

    /// <summary>
    /// The list query. "Representative" = most senior, then lowest bond id: one seek per deal on
    /// <c>ix_bond_deal_seniority (deal_id, seniority_rank, bond_id)</c>. ADR-0013 compares it with ROW_NUMBER().
    /// </summary>
    public const string ListSql = """
        WITH tranches AS (
            SELECT deal_id, count(*)::int AS bonds, sum(current_balance) AS current_balance
            FROM core.bond GROUP BY deal_id),
        held AS (
            SELECT deal_id, count(*)::int AS positions, sum(face) AS face, sum(market_value) AS mv
            FROM core.position_snapshot
            WHERE as_of_date = @asOf AND portfolio_id = ANY(@portfolios)
            GROUP BY deal_id)
        SELECT d.deal_id AS DealId, d.name AS Name, d.sector AS Sector, d.sub_sector AS SubSector, d.vintage AS Vintage,
               d.status AS Status, d.original_balance AS OriginalBalance,
               coalesce(t.bonds, 0) AS BondCount, t.current_balance AS CurrentBalance,
               b.bond_id AS RepBondId, b.class AS RepClass, b.cusip::text AS RepCusip, b.current_balance AS RepCurrentBalance,
               b.coupon_type AS RepCouponType, b.coupon_or_margin AS RepCoupon, b.rating_sp AS RepRating,
               h.positions AS Positions, h.face AS HeldFace, h.mv AS HeldMarketValue
        FROM core.deal d
        LEFT JOIN LATERAL (
            SELECT bond_id, class, cusip, current_balance, coupon_type, coupon_or_margin, rating_sp
            FROM core.bond
            WHERE deal_id = d.deal_id
            ORDER BY seniority_rank, bond_id
            LIMIT 1) b ON true
        LEFT JOIN tranches t ON t.deal_id = d.deal_id
        LEFT JOIN held h ON h.deal_id = d.deal_id
        ORDER BY d.deal_id
        """;

    public async Task<DealRow[]> ListAsync(DateOnly asOf, int[] portfolios, CancellationToken ct)
    {
        await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        var rows = await conn.QueryAsync<DealRow>(new CommandDefinition(ListSql, new { asOf, portfolios }, cancellationToken: ct));
        return [.. rows];
    }

    /// <summary>
    /// The deal, its tranches and the caller's positions (core) and its surveillance trend (surveillance), each source
    /// on its own connection, in parallel. Null when the deal doesn't exist.
    /// </summary>
    public async Task<DealDetail?> DetailAsync(int dealId, DateOnly asOf, int[] portfolios, CancellationToken ct)
    {
        var core = CoreAsync(dealId, asOf, portfolios, ct);
        var trend = TrendAsync(dealId, asOf, ct);
        await Task.WhenAll(core, trend);
        var (deal, tranches, positions) = await core;
        return deal is null ? null : new DealDetail(asOf, deal, tranches, await trend, positions);
    }

    private async Task<(DealInfo? Deal, Tranche[] Tranches, DealPosition[] Positions)> CoreAsync(
        int dealId, DateOnly asOf, int[] portfolios, CancellationToken ct)
    {
        await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        await using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT deal_id AS DealId, name AS Name, sector AS Sector, sub_sector AS SubSector, vintage AS Vintage,
                   closing_date AS ClosingDate, collateral_type AS CollateralType, original_balance AS OriginalBalance,
                   currency AS Currency, status AS Status
            FROM core.deal WHERE deal_id = @dealId;

            SELECT bond_id AS BondId, class AS Class, cusip::text AS Cusip, seniority_rank AS SeniorityRank,
                   original_balance AS OriginalBalance, current_balance AS CurrentBalance, factor AS Factor,
                   coupon_type AS CouponType, coupon_or_margin AS CouponOrMargin, coupon_index AS CouponIndex,
                   rating_sp AS RatingSp, rating_moodys AS RatingMoodys, rating_fitch AS RatingFitch,
                   legal_final AS LegalFinal, expected_maturity AS ExpectedMaturity
            FROM core.bond WHERE deal_id = @dealId
            ORDER BY seniority_rank, bond_id;

            SELECT s.position_id AS PositionId, s.portfolio_id AS PortfolioId, p.name AS Portfolio, s.bond_id AS BondId,
                   s.class AS Class, s.face AS Face, s.market_value AS MarketValue, s.price AS Price
            FROM core.position_snapshot s JOIN core.portfolio p ON p.portfolio_id = s.portfolio_id
            WHERE s.as_of_date = @asOf AND s.deal_id = @dealId AND s.portfolio_id = ANY(@portfolios)
            ORDER BY s.portfolio_id, s.position_id;
            """, new { dealId, asOf, portfolios }, cancellationToken: ct));
        var deal = await multi.ReadSingleOrDefaultAsync<DealInfo>();
        var tranches = await multi.ReadAsync<Tranche>();
        var positions = await multi.ReadAsync<DealPosition>();
        return (deal, [.. tranches], [.. positions]);
    }

    /// <summary>The last <see cref="TrendMonths"/> remittance periods up to the as-of date, oldest first.</summary>
    private async Task<RemitMonth[]> TrendAsync(int dealId, DateOnly asOf, CancellationToken ct)
    {
        await using var conn = await sources.OpenAsync(ConnectionStrings.Surveillance, ct);
        var rows = await conn.QueryAsync<RemitMonth>(new CommandDefinition(
            """
            SELECT * FROM (
                SELECT period AS Period, loan_count AS LoanCount, dq_30 AS Dq30, dq_60 AS Dq60, dq_90plus AS Dq90Plus,
                       cpr AS Cpr, cdr AS Cdr, severity AS Severity, oc_cushion AS OcCushion, ic_cushion AS IcCushion
                FROM surveillance.deal_remit
                WHERE deal_id = @dealId AND period <= @asOf
                ORDER BY period DESC
                LIMIT @months) last
            ORDER BY Period
            """, new { dealId, asOf, months = TrendMonths }, cancellationToken: ct));
        return [.. rows];
    }
}
