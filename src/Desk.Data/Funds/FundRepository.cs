using Dapper;
using Desk.Data.Sources;

namespace Desk.Data.Funds;

public sealed record FundMonth(DateOnly AsOfMonth, decimal Balance, double IrrItd);

public sealed record FundSpan(int FundId, string Name, DateOnly First, DateOnly Last);

/// <summary>P2 reads on the <c>core</c> source (README §6 P2): long format, one row per month-end; pivoted at the edge (ADR-0011).</summary>
public sealed class FundRepository(IDataSourceRegistry sources)
{
    /// <summary>The fund's name and its first and last month-ends; null when the fund has no performance data.</summary>
    public async Task<FundSpan?> SpanAsync(int fundId, CancellationToken ct)
    {
        await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        return await conn.QuerySingleOrDefaultAsync<FundSpan>(new CommandDefinition(
            """
            SELECT f.fund_id AS FundId, f.name AS Name, min(p.as_of_month) AS First, max(p.as_of_month) AS Last
            FROM core.fund f JOIN core.fund_performance p ON p.fund_id = f.fund_id
            WHERE f.fund_id = @fundId
            GROUP BY f.fund_id, f.name
            """, new { fundId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FundMonth>> MonthsAsync(int fundId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        var rows = await conn.QueryAsync<FundMonth>(new CommandDefinition(
            """
            SELECT as_of_month AS AsOfMonth, balance AS Balance, irr_itd AS IrrItd
            FROM core.fund_performance
            WHERE fund_id = @fundId AND as_of_month BETWEEN @from AND @to
            ORDER BY as_of_month
            """, new { fundId, from, to }, cancellationToken: ct));
        return rows.ToArray();
    }
}
