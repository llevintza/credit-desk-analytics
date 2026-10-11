using Desk.Api.Auth;

namespace Desk.Api.Hardening;

/// <summary>
/// OpenAPI document and Swagger UI (README §8, ADR-0019). On in Development; elsewhere only with
/// <c>SWAGGER_ENABLED=true</c>. Non-booleans fail safe to off.
/// The document is admin-only because the site is public (#94). Since bearer tokens (ADR-0024) a page navigation
/// carries no credentials, so the UI shell (the stock Swagger UI bundle, no API data) is served without a session,
/// and <see cref="AuthScriptPath"/> signs its requests with the session of the tab it is opened in.
/// </summary>
public static class SwaggerSetup
{
    public const string ConfigKey = "SWAGGER_ENABLED";
    /// <summary>Served under /swagger so it gets the Swagger CSP.</summary>
    public const string AuthScriptPath = "/swagger/desk-auth.js";
    /// <summary>The SPA's sessionStorage key for the refresh token (web/src/app/core/token.store.ts).</summary>
    public const string RefreshTokenKey = "desk.refresh";

    // Swagger UI's own requestInterceptor option is evaluated with Function(), which a CSP without 'unsafe-eval'
    // blocks. A same-origin script loaded in <head> wraps fetch instead, before the UI starts: same-origin /api and
    // /openapi requests get a bearer token from the refresh token the SPA left in this tab's sessionStorage (open
    // /swagger in the tab you signed in in). The rotated refresh token goes back, so the SPA keeps working after.
    internal const string AuthScript = """
        (function () {
          var original = window.fetch;
          var key = 'desk.refresh';
          var access = null;
          var expires = 0;
          var pending = null;
          function refresh() {
            var token = null;
            try { token = sessionStorage.getItem(key); } catch (e) { token = null; }
            if (!token) return Promise.resolve(null);
            pending = pending || original.call(window, '/api/auth/refresh', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ refreshToken: token })
            }).then(function (r) { return r.ok ? r.json() : null; }).then(function (t) {
              pending = null;
              access = t ? t.accessToken : null;
              // Renewed 30 s before it runs out, so a call never goes with an expired token.
              expires = t ? Date.now() + (t.expiresIn - 30) * 1000 : 0;
              try { if (t) sessionStorage.setItem(key, t.refreshToken); } catch (e) { /* storage blocked */ }
              return access;
            }, function () { pending = null; return null; });
            return pending;
          }
          window.fetch = function (input, init) {
            var url = new URL(typeof input === 'string' ? input : input.url, location.href);
            var ours = url.origin === location.origin && /^\/(api|openapi)\//.test(url.pathname) && url.pathname !== '/api/auth/refresh';
            if (!ours) return original.call(this, input, init);
            var self = this;
            return (access && Date.now() < expires ? Promise.resolve(access) : refresh()).then(function (token) {
              init = init || {};
              var headers = new Headers(init.headers || (typeof input !== 'string' && input.headers) || {});
              if (token) headers.set('Authorization', 'Bearer ' + token);
              init.headers = headers;
              return original.call(self, input, init);
            });
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
            branch.Use(ServeAuthScriptAsync);
            branch.UseSwaggerUI(o =>
            {
                o.SwaggerEndpoint("/openapi/v1.json", "Credit Desk Analytics API v1");
                o.RoutePrefix = "swagger";
                o.DocumentTitle = "Credit Desk Analytics API";
                o.InjectJavascript(AuthScriptPath);
            });
        });
    }

    internal static async Task ServeAuthScriptAsync(HttpContext http, RequestDelegate next)
    {
        if (http.Request.Path.Equals(AuthScriptPath, StringComparison.OrdinalIgnoreCase))
        {
            http.Response.ContentType = "text/javascript; charset=utf-8";
            await http.Response.WriteAsync(AuthScript, http.RequestAborted);
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
