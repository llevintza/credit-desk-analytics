using System.IO.Compression;
using Desk.Api;
using Desk.Api.Admin;
using Desk.Api.Audit;
using Desk.Api.Auth;
using Desk.Api.Funds;
using Desk.Data.Funds;
using Desk.Api.Hardening;
using Desk.Api.Limits;
using Desk.Api.Positions;
using Desk.Data.Grid;
using Desk.Data;
using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

// Structured logs outside Development: one JSON object per line, with the request id scope (README §7.3).
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
}

builder.Services.AddDeskData(builder.Configuration);
builder.Services.AddDeskAuth();
builder.Services.AddSingleton<DemoAccounts>();
builder.Services.AddDeskRateLimiting(LimitsOptions.From(builder.Configuration));
builder.Services.AddSingleton<AuditQueue>();
builder.Services.AddHostedService<AuditWriter>();
builder.Services.AddSingleton<GridRepository>();
builder.Services.AddSingleton<FundRepository>();
builder.Services.AddSingleton<MetaRepository>();
builder.Services.AddSingleton<PresetRepository>();
builder.Services.AddSingleton<MetaCache>();
builder.Services.AddSingleton<PositionsCache>();
builder.Services.AddSingleton<IPortfolioEntitlements, AllPortfolios>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, DeskJsonContext.Default));
builder.Services.AddProblemDetails();
// OpenAPI document (README §8) at /openapi/v1.json, browsable at /swagger (admin only).
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Credit Desk Analytics API";
    doc.Info.Description = "Front-office analytics API for a structured-credit desk. Everything under /api except login needs a session.";
    // Behind a TLS-terminating proxy the generated server is http://; Swagger UI then
    // "Try it out"s over HTTP. Empty servers make the UI use the page's origin (relative).
    doc.Servers = [];
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

// Order matters: maintenance answers before anything that can reach the database; rate limiting partitions by
// the authenticated user; the audit middleware sees only admitted requests; authorization runs last.
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeaders>();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseMiddleware<MaintenanceMode>();
app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
// Only paths that need a session decrypt the cookie: /health and the SPA never load the key ring or run the
// security-stamp query, so they can't wake the database (maintenance mode, platform probes).
app.UseWhen(MaintenanceMode.IsSessionPath, b => b.UseAuthentication());
// UseAuthentication on a branch doesn't mark the app, and WebApplication would then add a global one at the very
// start of the pipeline (before maintenance mode). Mark it so the path-scoped one above is the only one.
((IApplicationBuilder)app).Properties["__AuthenticationMiddlewareSet"] = true;
app.UseRateLimiter();
// After the limiter: rejected (429) requests are not written to the audit table.
app.UseMiddleware<AuditMiddleware>();
app.UseAuthorization();
app.UseDeskSwagger();

var api = app.MapGroup("/api")
    .RequireAuthorization()
    .AddEndpointFilter<AntiforgeryFilter>();
app.MapHealthEndpoints(api);
api.MapAuthEndpoints();
api.MapMetaEndpoints();
api.MapPositionsEndpoints();
api.MapFundEndpoints();
api.MapAdminEndpoints();
api.MapFallback(() => Results.NotFound()).ExcludeFromDescription();

// Client-side routes fall back to the SPA; /api/* never does. /swagger and /openapi are
// 404 endpoints when Swagger is off, so they never reach this fallback either.
app.MapFallbackToFile("index.html").ExcludeFromDescription();

app.Run();

public partial class Program;
