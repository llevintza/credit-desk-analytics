using System.IO.Compression;
using Desk.Data;
using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDeskData(builder.Configuration);
builder.Services.AddProblemDetails();
// OpenAPI document (README §8) at /openapi/v1.json, browsable at /swagger.
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Credit Desk Analytics API";
    doc.Info.Description = "Front-office analytics API for a structured-credit desk. Data endpoints require a session (phase 2).";
    // Behind a TLS-terminating proxy the generated server is http://; Swagger UI then
    // "Try it out"s over HTTP. Empty servers make the UI use the page's origin (relative).
    doc.Servers?.Clear();
    return Task.CompletedTask;
}));
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

var app = builder.Build();

app.UseExceptionHandler();
app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();

// Static on purpose: platform probes must never wake the database (README §7.2).
// `version` is the git SHA baked in at image build time; the deploy pipeline waits for it (README §14.2).
var version = app.Configuration["APP_VERSION"] ?? "dev";
app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", version)))
   .WithName("Health").WithTags("Health")
   .WithSummary("Liveness probe with the deployed build version; never touches the database.");

// Swagger UI for exploring and trying the API (README §8, ADR-0019). On in Development; elsewhere only when
// SWAGGER_ENABLED=true (the site is public and there is no login until phase 2, which moves it behind admin).
// Production stays off unless the value is exactly a boolean true. Non-booleans fail safe to off.
if (IsSwaggerEnabled(app.Configuration, app.Environment, app.Logger))
{
    app.MapOpenApi();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "Credit Desk Analytics API v1");
        o.RoutePrefix = "swagger";
        o.DocumentTitle = "Credit Desk Analytics API";
    });
}
else
{
    // Strict 404 for the whole prefix so /swagger and /swagger/foo never look like missing SPA routes.
    MapNotFoundPrefix(app, "/swagger");
    MapNotFoundPrefix(app, "/openapi");
}

var api = app.MapGroup("/api").WithTags("Account");
// Placeholder until phase 2 (accounts): every data endpoint is behind auth, so /api/me is 401 without a session.
api.MapGet("/me", () => Results.Unauthorized())
   .WithName("GetCurrentUser")
   .WithSummary("Current user, roles and account expiry. Returns 401 until accounts land in phase 2.")
   .Produces(StatusCodes.Status401Unauthorized);
api.MapFallback(() => Results.NotFound()).ExcludeFromDescription();

// Client-side routes fall back to the SPA; /api/* never does. /swagger and /openapi are
// 404 endpoints when Swagger is off, so they never reach this fallback either.
app.MapFallbackToFile("index.html").ExcludeFromDescription();

app.Run();

static bool IsSwaggerEnabled(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
{
    var raw = configuration["SWAGGER_ENABLED"];
    if (raw is null)
        return environment.IsDevelopment();
    if (bool.TryParse(raw, out var enabled))
        return enabled;

    logger.LogWarning("SWAGGER_ENABLED value '{Value}' is not true or false; Swagger stays off.", raw);
    return false;
}

static void MapNotFoundPrefix(WebApplication app, string prefix)
{
    app.Map(prefix, () => Results.NotFound()).ExcludeFromDescription();
    app.Map($"{prefix}/{{**rest}}", () => Results.NotFound()).ExcludeFromDescription();
}

public sealed record HealthResponse(string Status, string Version);

public partial class Program;
