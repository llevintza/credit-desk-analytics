using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Desk.Data.Auth;

namespace Desk.Api.Auth;

/// <summary>
/// Identity's <see cref="UserManager{TUser}"/> with the lockout stamp and check on the injected <see cref="TimeProvider"/>
/// (#261), the clock token expiry and the session limits already use. Identity 10 reads
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
        DeskUser.IsLockedOutAt(await GetLockoutEnabledAsync(user), await GetLockoutEndDateAsync(user), time.GetUtcNow());

    /// <summary>
    /// The base, which counts the failure and records Identity's <c>increment_access_failed</c> update metric (#338),
    /// then, when that failure locked the account, restamps the lockout end on the injected clock. The base resets the
    /// count to 0 only when it locks, so a 0 after a successful call means this failure locked it. That costs one more
    /// UPDATE on the locking attempt only, through the store and not <see cref="UserManager{TUser}.SetLockoutEndDateAsync"/>,
    /// so no second metric is recorded.
    /// </summary>
    public override async Task<IdentityResult> AccessFailedAsync(DeskUser user)
    {
        var result = await base.AccessFailedAsync(user);
        if (!result.Succeeded || await GetAccessFailedCountAsync(user) != 0)
            return result;
        // The EF store implements the lockout store; the base requires it too.
        var lockouts = (IUserLockoutStore<DeskUser>)Store;
        await lockouts.SetLockoutEndDateAsync(user, time.GetUtcNow().Add(Options.Lockout.DefaultLockoutTimeSpan), CancellationToken);
        return await UpdateUserAsync(user);
    }
}
