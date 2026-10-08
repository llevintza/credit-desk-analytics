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
    /// Identity's check with exactly one PBKDF2 verification on every path (R105-F2): the same hashing work, whether
    /// the account exists or what state it is in. The DB work still differs (a wrong password writes the failure
    /// count), so <see cref="LoginFloor"/> pads every failed login to the same response time (#230).
    /// </summary>
    /// <remarks>
    /// The base method returns before hashing for a disabled, expired or locked account, and the attempt that
    /// triggers lockout hashes there and again in a caller that guards the locked case. Here the account is checked
    /// once, then either the stored hash is verified or the <see cref="TimingGuard"/> decoy is, never both. Calling
    /// the base after the pre-check would re-run it, and an expiry passing in between would skip the hash.
    /// <para>
    /// Unlike the base, this doesn't record Identity's <c>aspnetcore.identity.sign_in.check_password_attempts</c>
    /// metric: its recorder is internal to <see cref="SignInManager{TUser}"/>. The base's logging is kept
    /// (<see cref="SignInManager{TUser}.LockedOut"/> on the attempt that triggers lockout).
    /// </para>
    /// <para>
    /// An account with no stored password fails without counting toward lockout (the base increments the count).
    /// Such an account can never sign in with a password, so there is nothing to lock it out of.
    /// </para>
    /// </remarks>
    public override async Task<SignInResult> CheckPasswordSignInAsync(DeskUser user, string password, bool lockoutOnFailure)
    {
        ArgumentNullException.ThrowIfNull(user);
        var refused = await PreSignInCheck(user);
        if (refused is not null || string.IsNullOrEmpty(password) || user.PasswordHash is null)
        {
            // The endpoint coalesces a missing password to "", other callers may not: the hasher throws on null.
            guard.Verify(password ?? string.Empty);
            return refused ?? SignInResult.Failed;
        }

        if (await UserManager.CheckPasswordAsync(user, password))
        {
            // No two-factor in this app, so a correct password always clears the failure count (as the base does,
            // and only where the store supports lockout).
            // As the base: a reset that fails (e.g. a concurrency conflict from parallel guesses) must not sign in.
            if (!UserManager.SupportsUserLockout)
                return SignInResult.Success;
            var reset = await UserManager.ResetAccessFailedCountAsync(user);
            return reset.Succeeded ? SignInResult.Success : SignInResult.Failed;
        }

        if (UserManager.SupportsUserLockout && lockoutOnFailure)
        {
            // As the base: an increment that fails (a concurrency conflict) fails the attempt, rather than reading
            // the lockout from unsaved in-memory state.
            var counted = await UserManager.AccessFailedAsync(user);
            if (!counted.Succeeded)
                return SignInResult.Failed;
            if (await UserManager.IsLockedOutAsync(user))
                return await LockedOut(user);
        }
        return SignInResult.Failed;
    }
}
