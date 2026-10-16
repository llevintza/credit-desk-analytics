using Desk.Api.Insights;
using Desk.Data.Auth;

namespace Desk.Api.Lab;

/// <summary>
/// The server side of the Performance Lab's toggles (README §6 P5, ADR-0028). Both are request headers, so the lab
/// measures the same endpoints the pages use, and each is honoured only where the spec allows it:
/// <list type="bullet">
/// <item><c>X-Lab-Cache: off</c> skips the response and summary caches, for admins only (a viewer's header is
/// ignored). The rate limits and database permits still apply.</item>
/// <item><c>X-Lab-Encoding: br | gzip | identity</c> replaces the browser's <c>Accept-Encoding</c> (which scripts
/// can't set), only where the dev knobs are on: Development or <c>DEV_FAULT_INJECTION=true</c>
/// (<see cref="InsightsFaults"/>). The applied value is echoed in the response.</item>
/// </list>
/// </summary>
public static class LabSwitches
{
    public const string CacheHeader = "X-Lab-Cache";
    public const string EncodingHeader = "X-Lab-Encoding";

    /// <summary>What <c>X-Cache</c> says when the cache was skipped.</summary>
    public const string Bypass = "BYPASS";

    private static readonly string[] Encodings = ["br", "gzip", "identity"];

    /// <summary>True when an admin asked for this request to skip the cache.</summary>
    public static bool BypassCache(HttpContext http) =>
        string.Equals(http.Request.Headers[CacheHeader], "off", StringComparison.Ordinal) && http.User.IsInRole(Roles.Admin);

    /// <summary>Before response compression: applies <c>X-Lab-Encoding</c> when the dev knobs are on, else does nothing.</summary>
    public static void UseLabEncoding(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<InsightsFaults>().Enabled)
            return;
        app.Use((http, next) =>
        {
            var asked = http.Request.Headers[EncodingHeader].ToString();
            if (Array.IndexOf(Encodings, asked) >= 0)
            {
                http.Request.Headers.AcceptEncoding = asked;
                http.Response.Headers[EncodingHeader] = asked;
            }
            return next(http);
        });
    }
}
