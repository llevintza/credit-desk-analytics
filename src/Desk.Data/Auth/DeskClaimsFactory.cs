using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Desk.Data.Auth;

/// <summary>
/// Adds the account expiry to the principal. Runs at login and again whenever the security-stamp validator
/// refreshes the cookie, so the claim always reflects the database.
/// </summary>
public sealed class DeskClaimsFactory(
    UserManager<DeskUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<DeskUser, IdentityRole<Guid>>(users, roles, options)
{
    public const string ExpiresAtClaim = "desk:expires_at";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(DeskUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ExpiresAtClaim, user.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)));
        return identity;
    }

    public static DateTimeOffset? ReadExpiry(ClaimsPrincipal principal) =>
        DateTimeOffset.TryParse(principal.FindFirstValue(ExpiresAtClaim), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;
}
