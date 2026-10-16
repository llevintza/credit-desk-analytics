namespace Desk.Data.App;

/// <summary>
/// One portfolio a user may see (ADR-0021 model B). Loaded into the sign-in claims (<c>desk:portfolio</c>) at login and at
/// every token refresh; never read per request. No FK to <c>core.portfolio</c>: the seeder truncates it and <c>core</c> may
/// move to its own database, so ids lost in a reseed are dropped when the claims are intersected with the snapshot.
/// </summary>
public sealed class PortfolioGrant
{
    public Guid UserId { get; set; }
    public int PortfolioId { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    /// <summary>Who wrote the grant (<c>Desk.UserAdmin</c>, <c>DEMO_ACCOUNTS_JSON</c>); never a password or secret.</summary>
    public string? GrantedBy { get; set; }
}
