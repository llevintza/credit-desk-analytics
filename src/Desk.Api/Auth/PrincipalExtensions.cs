using System.Diagnostics.CodeAnalysis;
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

    /// <summary>
    /// The id a signed-in caller is keyed on (rate limits, <c>ExportGate</c>). False for an anonymous caller, and for a
    /// signed-in one without an id (<see cref="HasNoAccountId"/>): every key that follows the account goes through
    /// here, so no path can key that caller on anything else (#361).
    /// </summary>
    public static bool TryGetAccountId(this ClaimsPrincipal user, [NotNullWhen(true)] out string? id)
    {
        id = user.IsSignedIn() ? user.UserId() : null;
        return !string.IsNullOrEmpty(id);
    }

    /// <summary>
    /// Signed in, but with no account id. Issued tokens always carry one (DeskClaimsFactory), so this is a broken
    /// session: it is refused with <see cref="NoAccountId"/>, never keyed on the client address or the user name.
    /// </summary>
    public static bool HasNoAccountId(this ClaimsPrincipal user) => user.IsSignedIn() && !user.TryGetAccountId(out _);

    public const string NoAccountIdTitle = "Session has no account";

    /// <summary>
    /// The answer to <see cref="HasNoAccountId"/>: 401, as SessionLimits gives a token it can't trust, so the SPA
    /// signs out and the user signs in again for a token that has the id.
    /// </summary>
    public static IResult NoAccountId() => Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
        title: NoAccountIdTitle, detail: "Sign in again.");
}
