namespace Desk.Data.App;

/// <summary>
/// One audited event (README §7.2): an API request, or a login attempt. Feeds the admin Usage page.
/// Written in batches off the request path, so a slow or failing insert never delays a response.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Kind { get; set; }
    public string? UserName { get; set; }
    public required string Endpoint { get; set; }
    public int Status { get; set; }
    public int? Rows { get; set; }
    public int Ms { get; set; }
    public string? Cache { get; set; }
}

public static class AuditKinds
{
    public const string Request = "request";
    public const string LoginSuccess = "login_success";
    public const string LoginFailure = "login_failure";
}
