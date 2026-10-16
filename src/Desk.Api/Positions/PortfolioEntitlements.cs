using System.Globalization;
using System.Security.Claims;
using Desk.Data.Auth;

namespace Desk.Api.Positions;

/// <summary>
/// Which portfolios a user may see (README §6 P1 cache key, §8 /api/meta/portfolios; ADR-0021). Every portfolio-owned
/// read resolves its scope here and applies it in SQL; the cache keys and ETags carry the resulting set.
/// <c>ENTITLEMENTS_MODE</c> picks the implementation (<see cref="EntitlementsMode"/>).
/// </summary>
/// <remarks>MUST stay synchronous and do no I/O (ADR-0021): it runs on every positions request, including
/// cache HITs and before the 304 check. Read entitlements from the principal's claims (loaded at sign-in and
/// refreshed by the security-stamp validator) or from <see cref="MetaSnapshot"/>; never query a database here.</remarks>
public interface IPortfolioEntitlements
{
    IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta);
}

/// <summary><c>ENTITLEMENTS_MODE=all</c> (the default): every account sees every portfolio.</summary>
public sealed class AllPortfolios : IPortfolioEntitlements
{
    public IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta) => meta.UnscopedPortfolios.Select(p => p.PortfolioId).ToHashSet();
}

/// <summary>
/// <c>ENTITLEMENTS_MODE=grants</c> (ADR-0021 model B): an admin sees every portfolio (E1); anyone else sees the portfolios
/// in their <c>desk:portfolio</c> claims, which <see cref="DeskClaimsFactory"/> reads from <c>app.portfolio_grant</c> at
/// sign-in and at every token refresh. No grants means no portfolios (E2, fail closed). The claims are intersected
/// with the snapshot, so a grant for a portfolio lost in a reseed drops out.
/// </summary>
public sealed class GrantedPortfolios : IPortfolioEntitlements
{
    public IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta)
    {
        var known = meta.UnscopedPortfolios.Select(p => p.PortfolioId).ToHashSet();
        if (user.IsInRole(Roles.Admin))
            return known;

        var granted = new HashSet<int>();
        foreach (var claim in user.FindAll(DeskClaimsFactory.PortfolioClaim))
            if (int.TryParse(claim.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && known.Contains(id))
                granted.Add(id);
        return granted;
    }
}

/// <summary>Reads <c>ENTITLEMENTS_MODE</c> (<c>all</c> | <c>grants</c>; unset means <c>all</c>). Anything else fails startup.</summary>
public static class EntitlementsMode
{
    public const string ConfigKey = "ENTITLEMENTS_MODE";

    public static IPortfolioEntitlements Create(IConfiguration config)
    {
        var mode = (config[ConfigKey] ?? "").Trim().ToLowerInvariant();
        if (mode.Length == 0 || mode == "all")
            return new AllPortfolios();
        if (mode == "grants")
            return new GrantedPortfolios();
        // Fail loudly: a typo must never fall back to "all" (everyone sees everything) or to "grants" (everyone sees nothing).
        throw new InvalidOperationException($"{ConfigKey} must be 'all' or 'grants' (ADR-0021); got '{mode}'.");
    }
}
