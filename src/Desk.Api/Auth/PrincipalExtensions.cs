using System.Security.Claims;

namespace Desk.Api.Auth;

public static class PrincipalExtensions
{
    /// <summary>True when any identity on the principal is authenticated (a live bearer token, ADR-0024).</summary>
    public static bool IsSignedIn(this ClaimsPrincipal user) => user.Identities.Any(i => i.IsAuthenticated);
}
