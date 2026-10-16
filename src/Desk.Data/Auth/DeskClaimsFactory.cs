using System.Globalization;
using System.Security.Claims;
using Desk.Data.App;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Desk.Data.Auth;

/// <summary>
/// Adds the account expiry and the portfolio grants (ADR-0021) to the principal. Runs at login and again at every token
/// refresh (ADR-0024), so the claims reflect the database within one access-token lifetime: a grant or revoke applies
/// at the user's next refresh, without signing them out (ADR-0021 E5).
/// </summary>
public sealed class DeskClaimsFactory(
    UserManager<DeskUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IOptions<IdentityOptions> options,
    AppDbContext db)
    : UserClaimsPrincipalFactory<DeskUser, IdentityRole<Guid>>(users, roles, options)
{
    public const string ExpiresAtClaim = "desk:expires_at";
    /// <summary>One claim per granted portfolio id (ADR-0021); read by <c>GrantedPortfolios</c>, never by a database query per request.</summary>
    public const string PortfolioClaim = "desk:portfolio";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(DeskUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ExpiresAtClaim, user.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)));
        // Identity's factory API takes no token; this runs once per login or refresh (every 5 minutes per active user).
        var granted = await db.PortfolioGrants.AsNoTracking()
            .Where(g => g.UserId == user.Id)
            .OrderBy(g => g.PortfolioId)
            .Select(g => g.PortfolioId)
            .ToListAsync();
        foreach (var id in granted)
            identity.AddClaim(new Claim(PortfolioClaim, id.ToString(CultureInfo.InvariantCulture)));
        return identity;
    }

    public static DateTimeOffset? ReadExpiry(ClaimsPrincipal principal) =>
        DateTimeOffset.TryParse(principal.FindFirstValue(ExpiresAtClaim), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;
}
