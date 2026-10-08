using Microsoft.AspNetCore.Identity;

namespace Desk.Data.Auth;

/// <summary>Identity rules shared by the API and the UserAdmin CLI (README §7.1), so both enforce the same policy.</summary>
public static class IdentityPolicy
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public const int MinPasswordLength = 14;

    public static void Apply(IdentityOptions o)
    {
        // Identity defaults for character classes; only the length is raised.
        o.Password.RequiredLength = MinPasswordLength;
        o.Lockout.MaxFailedAccessAttempts = MaxFailedAttempts;
        o.Lockout.DefaultLockoutTimeSpan = LockoutDuration;
        o.Lockout.AllowedForNewUsers = true;
        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedAccount = false;
    }
}
