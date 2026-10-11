using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Desk.Data.Auth;

namespace Desk.Api.Auth;

/// <summary>
/// Identity's <see cref="UserManager{TUser}"/> with the lockout stamp and check on the injected <see cref="TimeProvider"/>
/// (#261), the clock cookie expiry and the security-stamp validator already use. Identity 10 reads
/// <see cref="DateTimeOffset.UtcNow"/> in exactly these two members and has no clock option. Behaviour is otherwise the
/// base's, including the inclusive boundary: an account stays locked at the instant its lockout ends.
/// </summary>
public sealed class DeskUserManager(
    IUserStore<DeskUser> store,
    IOptions<IdentityOptions> optionsAccessor,
    IPasswordHasher<DeskUser> passwordHasher,
    IEnumerable<IUserValidator<DeskUser>> userValidators,
    IEnumerable<IPasswordValidator<DeskUser>> passwordValidators,
    ILookupNormalizer keyNormalizer,
    IdentityErrorDescriber errors,
    IServiceProvider services,
    ILogger<UserManager<DeskUser>> logger,
    TimeProvider time)
    : UserManager<DeskUser>(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
{
    public override async Task<bool> IsLockedOutAsync(DeskUser user) =>
        await GetLockoutEnabledAsync(user) && await GetLockoutEndDateAsync(user) >= time.GetUtcNow();

    /// <summary>As the base: count the failure; at the limit, lock until now + the lockout span and reset the count.</summary>
    public override async Task<IdentityResult> AccessFailedAsync(DeskUser user)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);
        // The EF store implements the lockout store; the base requires it too.
        var lockouts = (IUserLockoutStore<DeskUser>)Store;
        var count = await lockouts.IncrementAccessFailedCountAsync(user, CancellationToken);
        if (count >= Options.Lockout.MaxFailedAccessAttempts)
        {
            Logger.LogDebug("User is locked out.");
            await lockouts.SetLockoutEndDateAsync(user, time.GetUtcNow().Add(Options.Lockout.DefaultLockoutTimeSpan), CancellationToken);
            await lockouts.ResetAccessFailedCountAsync(user, CancellationToken);
        }
        return await UpdateUserAsync(user);
    }
}
