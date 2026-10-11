using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Desk.Api.Hardening;

/// <summary>
/// Cross-origin access for a front end on another site (GitHub Pages, ADR-0024). Only the origins in
/// <c>CORS_ALLOWED_ORIGINS</c> (comma or space separated, scheme + host [+ port], no path; https only outside
/// Development, where the dev server's http://localhost may be listed) are allowed; unset or
/// empty allows none, which is the same-origin app's setting. No credentials: the API takes bearer tokens in
/// <c>Authorization</c>, never cookies.
/// </summary>
public static class CorsSetup
{
    public const string ConfigKey = "CORS_ALLOWED_ORIGINS";

    /// <summary>Request headers the SPA sends: the token, JSON bodies, and the positions grid's conditional request.</summary>
    public static readonly string[] AllowedHeaders = ["Authorization", "Content-Type", "If-None-Match"];
    /// <summary>Response headers the SPA reads: the 429 wait, the status bar's cache and server time, the grid's ETag.</summary>
    public static readonly string[] ExposedHeaders = ["Retry-After", "X-Cache", "Server-Timing", "ETag"];

    /// <summary>
    /// The configured origins, normalised (no trailing slash, lower case). Anything that isn't an https origin is
    /// dropped, and so is an http one unless <paramref name="allowHttp"/> (Development only).
    /// </summary>
    public static string[] Origins(IConfiguration config, bool allowHttp) =>
        (config[ConfigKey] ?? "")
            .Split([',', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(o => Uri.TryCreate(o, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || (allowHttp && uri.Scheme == "http"))
                && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
                ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant()
                : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static IServiceCollection AddDeskCors(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var policy = Policy(Origins(config, env.IsDevelopment()));
        return services.AddCors(o => o.AddDefaultPolicy(policy));
    }

    /// <remarks>
    /// The exact-match check is set explicitly, so ASP.NET Core always sends <c>Vary: Origin</c>. With the default
    /// check it varies only when more than one origin is listed, and one origin (production) would let a cache
    /// hand a response without CORS headers to the cross-origin SPA.
    /// </remarks>
    internal static CorsPolicy Policy(string[] origins)
    {
        var allowed = origins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new CorsPolicyBuilder(origins)
            .SetIsOriginAllowed(allowed.Contains)
            .WithMethods("GET", "POST", "PUT", "DELETE")
            .WithHeaders(AllowedHeaders)
            .WithExposedHeaders(ExposedHeaders)
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))
            .Build();
    }

    /// <summary>
    /// Before maintenance mode and the rate limiter: a preflight is answered here (it never takes a token or a
    /// permit), and every real response, a 429 or 503 included, carries the CORS headers the browser needs to show
    /// it. An allowed origin also gets <c>Timing-Allow-Origin</c>, so the status bar can read transfer sizes.
    /// </summary>
    public static void UseDeskCors(this WebApplication app)
    {
        var origins = Origins(app.Configuration, app.Environment.IsDevelopment()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        app.Use((http, next) =>
        {
            // Absent is "", and several values join with a comma: neither is a configured origin.
            var origin = http.Request.Headers.Origin.ToString();
            if (origins.Contains(origin))
                http.Response.Headers["Timing-Allow-Origin"] = origin;
            return next(http);
        });
        app.UseCors();
    }
}
