namespace Desk.Api.Hardening;

/// <summary>
/// Security headers on every response (README §7.3, ADR-0005).
/// <list type="bullet">
/// <item>No inline or remote scripts anywhere.</item>
/// <item><c>style-src 'unsafe-inline'</c> is needed by Angular's runtime component styles and AG Grid's
/// positioning <c>style</c> attributes; styles can't run code.</item>
/// <item>Swagger UI (admin only) also needs <c>data:</c> images for its icons, so <c>/swagger</c> gets its own policy.</item>
/// </list>
/// </summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    public const string AppCsp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public const string SwaggerCsp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public const string PermissionsPolicy =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

    public Task InvokeAsync(HttpContext http)
    {
        var h = http.Response.Headers;
        h.ContentSecurityPolicy = http.Request.Path.StartsWithSegments("/swagger") ? SwaggerCsp : AppCsp;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "same-origin";
        h["Permissions-Policy"] = PermissionsPolicy;
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        return next(http);
    }
}
