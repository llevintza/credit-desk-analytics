using Desk.Api.Auth;
using Desk.Data.Auth;

namespace Desk.Api.Hardening;

/// <summary>
/// OpenAPI document and Swagger UI (README §8, ADR-0019), admin-only because the site is public (#94).
/// On in Development; elsewhere only with <c>SWAGGER_ENABLED=true</c>. Non-booleans fail safe to off.
/// </summary>
public static class SwaggerSetup
{
    public const string ConfigKey = "SWAGGER_ENABLED";
    /// <summary>Served under /swagger so it gets the Swagger CSP and the admin gate.</summary>
    public const string XsrfScriptPath = "/swagger/desk-xsrf.js";

    // Swagger UI's own requestInterceptor option is evaluated with Function(), which a CSP without
    // 'unsafe-eval' blocks. A same-origin script loaded in <head> wraps fetch instead, before the UI starts.
    internal const string XsrfScript = """
        (function () {
          var original = window.fetch;
          window.fetch = function (input, init) {
            init = init || {};
            var method = (init.method || (input && input.method) || 'GET').toUpperCase();
            if (method !== 'GET' && method !== 'HEAD') {
              var m = document.cookie.match(/(?:^|;\s*)XSRF-TOKEN=([^;]*)/);
              if (m) {
                var headers = new Headers(init.headers || (input && input.headers) || {});
                headers.set('X-XSRF-TOKEN', decodeURIComponent(m[1]));
                init.headers = headers;
              }
            }
            return original.call(this, input, init);
          };
        })();
        """;

    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        var raw = configuration[ConfigKey];
        if (raw is null)
            return environment.IsDevelopment();
        if (bool.TryParse(raw, out var enabled))
            return enabled;

        logger.LogWarning("SWAGGER_ENABLED value '{Value}' is not true or false; Swagger stays off.", raw);
        return false;
    }

    /// <summary>Call after UseAuthentication.</summary>
    public static void UseDeskSwagger(this WebApplication app)
    {
        if (!IsEnabled(app.Configuration, app.Environment, app.Logger))
        {
            // Strict 404 for the whole prefix so /swagger and /swagger/foo never look like missing SPA routes.
            MapNotFoundPrefix(app, "/swagger");
            MapNotFoundPrefix(app, "/openapi");
            return;
        }

        app.MapOpenApi().RequireAuthorization(AuthSetup.AdminPolicy);
        app.UseWhen(http => http.Request.Path.StartsWithSegments("/swagger"), branch =>
        {
            branch.Use(AdminGateAsync);
            branch.UseSwaggerUI(o =>
            {
                o.SwaggerEndpoint("/openapi/v1.json", "Credit Desk Analytics API v1");
                o.RoutePrefix = "swagger";
                o.DocumentTitle = "Credit Desk Analytics API";
                o.InjectJavascript(XsrfScriptPath);
            });
        });
    }

    internal static async Task AdminGateAsync(HttpContext http, RequestDelegate next)
    {
        if (!http.User.IsSignedIn())
        {
            await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign in as an admin to use Swagger UI").ExecuteAsync(http);
            return;
        }
        if (!http.User.IsInRole(Roles.Admin))
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Swagger UI is admin-only").ExecuteAsync(http);
            return;
        }
        if (http.Request.Path.Equals(XsrfScriptPath, StringComparison.OrdinalIgnoreCase))
        {
            http.Response.ContentType = "text/javascript; charset=utf-8";
            await http.Response.WriteAsync(XsrfScript, http.RequestAborted);
            return;
        }
        await next(http);
    }

    private static void MapNotFoundPrefix(WebApplication app, string prefix)
    {
        app.Map(prefix, () => Results.NotFound()).ExcludeFromDescription();
        app.Map($"{prefix}/{{**rest}}", () => Results.NotFound()).ExcludeFromDescription();
    }
}
