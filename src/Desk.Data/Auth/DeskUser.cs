using Microsoft.AspNetCore.Identity;

namespace Desk.Data.Auth;

/// <summary>An invite-only account (README §7.1). There is no registration path; accounts come from the UserAdmin CLI.</summary>
public sealed class DeskUser : IdentityUser<Guid>
{
    /// <summary>Login is refused at or after this instant. Reviewer accounts get short expiries.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set by <c>UserAdmin disable</c>. Disabled accounts cannot log in, and live sessions end at the next security-stamp check.</summary>
    public bool IsDisabled { get; set; }

    public bool IsActiveAt(DateTimeOffset now) => !IsDisabled && now < ExpiresAt;
}
