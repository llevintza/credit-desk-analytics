using System.Security.Claims;

namespace Desk.Api.Positions;

/// <summary>
/// Which portfolios a user may see (README §6 P1 cache key, §8 /api/meta/portfolios). Phase 3 grants every account
/// all portfolios (synthetic data, read-only roles). Swap this service for a per-user table later; the cache
/// key and every query already go through it.
/// </summary>
/// <remarks>MUST stay synchronous and do no I/O (ADR-0021): it runs on every positions request, including
/// cache HITs and before the 304 check. Read entitlements from the principal's claims (loaded at sign-in and
/// refreshed by the security-stamp validator) or from <see cref="MetaSnapshot"/>; never query a database here.</remarks>
public interface IPortfolioEntitlements
{
    IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta);
}

public sealed class AllPortfolios : IPortfolioEntitlements
{
    public IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta) => meta.Portfolios.Select(p => p.PortfolioId).ToHashSet();
}
