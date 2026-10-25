namespace Desk.Data.App;

/// <summary>
/// One anonymous request for a demo account (ADR-0029, #538). Holds the minimum personal data: the email, and the name
/// and purpose only if given; no IP address or user agent. Leo approves or rejects it in the admin view (#540); the
/// purge deletes it after the retention window (#542).
/// </summary>
public sealed class AccessRequest
{
    /// <summary>The longest email address SMTP allows (RFC 5321 path limit), ADR-0029 §4.</summary>
    public const int EmailMaxLength = 254;
    /// <summary>The name and the purpose, each (ADR-0029 §4, D6).</summary>
    public const int TextMaxLength = 500;

    public long Id { get; set; }
    /// <summary>citext, stored trimmed and lower case; at most one <see cref="AccessRequestStatus.Pending"/> row per address.</summary>
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Purpose { get; set; }
    public string Status { get; set; } = AccessRequestStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    /// <summary>The admin who approved or rejected it; never a password or secret.</summary>
    public string? DecidedBy { get; set; }
    /// <summary>The account an approval created (#540); cleared if that account is deleted.</summary>
    public Guid? AccountId { get; set; }
}

public static class AccessRequestStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public static readonly string[] All = [Pending, Approved, Rejected];
}
