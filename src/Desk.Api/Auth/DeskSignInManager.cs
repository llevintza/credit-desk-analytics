using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Desk.Data.Auth;

namespace Desk.Api.Auth;

/// <summary>Refuses sign-in for disabled or expired accounts (README §7.1). Identity reports these as <c>IsNotAllowed</c>.</summary>
public sealed class DeskSignInManager(
    UserManager<DeskUser> users,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<DeskUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<DeskUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<DeskUser> confirmation,
    TimeProvider time,
    TimingGuard guard)
    : SignInManager<DeskUser>(users, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(DeskUser user) =>
        user.IsActiveAt(time.GetUtcNow()) && await base.CanSignInAsync(user);

    /// <summary>
    /// Identity's check with exactly one PBKDF2 verification on every path, so the response time doesn't reveal
    /// whether an account exists or what state it is in (R105-F2).
    /// </summary>
    /// <remarks>
    /// The base method returns before hashing for a disabled, expired or locked account, and the attempt that
    /// triggers lockout hashes there and again in a caller that guards the locked case. Here the account is checked
    /// once, then either the stored hash is verified or the <see cref="TimingGuard"/> decoy is, never both. Calling
    /// the base after the pre-check would re-run it, and an expiry passing in between would skip the hash.
    /// </remarks>
    public override async Task<SignInResult> CheckPasswordSignInAsync(DeskUser user, string password, bool lockoutOnFailure)
    {
        ArgumentNullException.ThrowIfNull(user);
        var refused = await PreSignInCheck(user);
        if (refused is not null || string.IsNullOrEmpty(password) || user.PasswordHash is null)
        {
            guard.Verify(password);
            return refused ?? SignInResult.Failed;
        }

        if (await UserManager.CheckPasswordAsync(user, password))
        {
            // No two-factor in this app, so a correct password always clears the failure count (as the base does).
            await UserManager.ResetAccessFailedCountAsync(user);
            return SignInResult.Success;
        }

        if (lockoutOnFailure)
        {
            await UserManager.AccessFailedAsync(user);
            if (await UserManager.IsLockedOutAsync(user))
                return SignInResult.LockedOut;
        }
        return SignInResult.Failed;
    }
}
