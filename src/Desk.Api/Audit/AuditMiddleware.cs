using System.Diagnostics;
using Desk.Api.Auth;
using Desk.Data.App;

namespace Desk.Api.Audit;

/// <summary>Set by data endpoints so the audit row carries rows returned and cache status (README §7.2).</summary>
public sealed class AuditFeature
{
    public int? Rows { get; set; }
    public string? Cache { get; set; }
}

/// <summary>Endpoint metadata: the endpoint writes its own audit rows (login).</summary>
public sealed class SkipRequestAudit
{
    public static readonly SkipRequestAudit Instance = new();
}

/// <summary>
/// Records every authenticated <c>/api</c> request. Anonymous traffic is not audited: a bot hammering the public
/// site must not turn into database writes on the free tier. Login attempts are audited by the login endpoint.
/// </summary>
public sealed class AuditMiddleware(RequestDelegate next, AuditQueue queue, TimeProvider time)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.StartsWithSegments("/api"))
        {
            await next(http);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var feature = new AuditFeature();
        http.Features.Set(feature);
        var failed = false;
        try
        {
            await next(http);
        }
        catch
        {
            failed = true; // the exception handler upstream turns this into a 500
            throw;
        }
        finally
        {
            var endpoint = http.GetEndpoint();
            if (http.User.IsSignedIn() && endpoint?.Metadata.GetMetadata<SkipRequestAudit>() is null)
            {
                var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value;
                queue.Enqueue(AuditKinds.Request, http.User.Identity!.Name, $"{http.Request.Method} {route}",
                    failed ? StatusCodes.Status500InternalServerError : http.Response.StatusCode, started, time, feature.Rows, feature.Cache);
            }
        }
    }
}
