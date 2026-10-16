namespace Desk.Data.Auth;

/// <summary>
/// One sign-in's server-side state (ADR-0024, #354): the refresh id that may still be swapped, the one swapped for it
/// and when, and whether a logout or a replay ended it. Kept until the session's 24 h limit, so a restart or a second
/// instance knows both. Only ids and times, never token values.
/// </summary>
public sealed class AuthSession
{
    /// <summary>The session id (<c>desk:sid</c>), 32 hex characters.</summary>
    public required string Sid { get; set; }

    /// <summary>The refresh id whose token may be swapped next; null for a session known only from its logout.</summary>
    public string? Rid { get; set; }

    /// <summary>The refresh id swapped for <see cref="Rid"/>, which the grace window still accepts.</summary>
    public string? PreviousRid { get; set; }

    public DateTimeOffset RotatedAt { get; set; }

    /// <summary>The session's 24 h limit from sign-in; the row is pruned after it.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When a logout or a detected replay ended the session.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
