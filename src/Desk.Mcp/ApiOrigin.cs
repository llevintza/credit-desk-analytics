namespace Desk.Mcp;

/// <summary>
/// The API origin a command talks to (ADR-0031 §2). <c>http://</c> only for the loopback hosts; any host other than
/// those and the compose service names also needs <c>--live</c> (D17: only Leo points the server at production).
/// </summary>
internal sealed record ApiOrigin(Uri Uri)
{
    private static readonly HashSet<string> Loopback = new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "[::1]", "::1" };

    /// <summary>The compose service names of a local stack (docker-compose.yml).</summary>
    private static readonly HashSet<string> ComposeServices = new(StringComparer.OrdinalIgnoreCase) { "app" };

    /// <summary>scheme://host[:port], no trailing slash: the key the token file is stored under.</summary>
    public string Value => Uri.GetLeftPart(UriPartial.Authority);

    public override string ToString() => Value;

    public static bool IsLoopback(Uri uri) => Loopback.Contains(uri.Host);

    /// <summary>Parses and checks <paramref name="text"/>; on refusal returns null with a message naming the origin only.</summary>
    public static ApiOrigin? TryParse(string? text, bool live, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "--api needs an origin such as https://desk.example or http://localhost:8080.";
            return null;
        }
        if (uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "--api takes an origin only (scheme, host and port), with no path, query or user name.";
            return null;
        }
        var origin = new ApiOrigin(uri);
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri))
        {
            error = $"Refusing {origin}: http:// is allowed only for localhost, 127.0.0.1 and [::1]. Use https://.";
            return null;
        }
        if (!live && !IsLoopback(uri) && !ComposeServices.Contains(uri.Host))
        {
            error = $"Refusing {origin}: a host other than a local stack also needs --live (ADR-0031 D17).";
            return null;
        }
        return origin;
    }
}
