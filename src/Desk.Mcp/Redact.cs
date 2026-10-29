using System.Text.RegularExpressions;

namespace Desk.Mcp;

/// <summary>
/// Scrubs text that leaves the server (tool errors, stderr) of emails, token-like strings and connection-string
/// passwords (ADR-0031 §4 output rules), whatever the API or an exception put in it.
/// </summary>
internal static partial class Redact
{
    [GeneratedRegex(@"[^\s@""'<>(),;:]+@[^\s@""'<>(),;:]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    // Bearer and refresh tokens are long base64url runs; nothing the desk API reports legitimately looks like that.
    [GeneratedRegex(@"[A-Za-z0-9_\-+/=.]{40,}")]
    private static partial Regex Token();

    [GeneratedRegex(@"(?i)(password|pwd)\s*=\s*[^;\s]*")]
    private static partial Regex Password();

    public static string Text(string? text) =>
        string.IsNullOrEmpty(text) ? "" : Password().Replace(Token().Replace(Email().Replace(text, "[email]"), "[token]"), "$1=[redacted]");
}
