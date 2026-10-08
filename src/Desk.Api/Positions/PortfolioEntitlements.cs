using System.Security.Claims;

namespace Desk.Api.Positions;

/// <summary>
/// Which portfolios a user may see (README §6 P1 cache key, §8 /api/meta/portfolios). Phase 3 grants every account
/// all portfolios (synthetic data, read-only roles). Swap this service for a per-user table later; the cache
/// key and every query already go through it.
/// </summary>
public interface IPortfolioEntitlements
{
    IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta);
}

public sealed class AllPortfolios : IPortfolioEntitlements
{
    public IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta) => meta.Portfolios.Select(p => p.PortfolioId).ToHashSet();
}
