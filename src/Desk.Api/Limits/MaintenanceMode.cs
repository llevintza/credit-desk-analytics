namespace Desk.Api.Limits;

/// <summary>
/// Kill switch (README §7.2): with <c>MAINTENANCE_MODE=true</c> every <c>/api</c> (and Swagger) call gets a friendly 503 before
/// authentication, rate limiting or anything else that could open a database connection. <c>/health</c> keeps
/// answering and reports the flag, so the login page can show a banner.
/// </summary>
public sealed class MaintenanceMode(RequestDelegate next, IConfiguration config)
{
    public const string ConfigKey = "MAINTENANCE_MODE";
    public const string Detail = "Credit Desk Analytics is down for maintenance. Please try again later.";

    public async Task InvokeAsync(HttpContext http)
    {
        if (IsOn(config) && IsSessionPath(http))
        {
            http.Response.Headers.RetryAfter = "300";
            await Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Maintenance", detail: Detail)
                .ExecuteAsync(http);
            return;
        }
        await next(http);
    }

    /// <summary>Paths that use the session (and so the database): the API and the admin-only Swagger UI / OpenAPI.</summary>
    public static bool IsSessionPath(HttpContext http) =>
        http.Request.Path.StartsWithSegments("/api")
        || http.Request.Path.StartsWithSegments("/swagger")
        || http.Request.Path.StartsWithSegments("/openapi");

    /// <summary>Read per request, so a config reload takes effect without a restart. Only an exact boolean true turns it on.</summary>
    public static bool IsOn(IConfiguration config) => bool.TryParse(config[ConfigKey], out var on) && on;
}
