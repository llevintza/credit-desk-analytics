namespace Desk.Data.Auth;

/// <summary>The two roles (README §7.1): <c>viewer</c> reads every page; <c>admin</c> also gets usage, cache and toggles.</summary>
public static class Roles
{
    public const string Viewer = "viewer";
    public const string Admin = "admin";

    public static readonly IReadOnlyList<string> All = [Viewer, Admin];

    // Fixed ids: the roles are reference data created by the AuthAndAudit migration (like the column catalog,
    // README §14.4), so nothing at runtime has to create them.
    public static readonly Guid ViewerId = new("6f1d1e4a-0c55-4f5e-9a59-0d1f6b7a0001");
    public static readonly Guid AdminId = new("6f1d1e4a-0c55-4f5e-9a59-0d1f6b7a0002");

    public static bool IsKnown(string? role) => role is Viewer or Admin;
}
