using System.Security.Claims;

namespace Desk.Api.Auth;

public static class PrincipalExtensions
{
    /// <summary>True when any identity on the principal is authenticated (a live bearer token, ADR-0024).</summary>
    public static bool IsSignedIn(this ClaimsPrincipal user) => user.Identities.Any(i => i.IsAuthenticated);

    /// <summary>
    /// The account's id (<c>NameIdentifier</c>), for keys that must follow the account: the user name can be renamed
    /// or change case while an old session is still live, and would then count as a second user (#185).
    /// </summary>
    public static string? UserId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);
}
