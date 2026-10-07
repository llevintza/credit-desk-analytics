using System.IO.Compression;
using Desk.Data;
using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDeskData(builder.Configuration);
builder.Services.AddProblemDetails();
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
app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", version)));

var api = app.MapGroup("/api");
// Placeholder until phase 2 (accounts): every data endpoint is behind auth, so /api/me is 401 without a session.
api.MapGet("/me", () => Results.Unauthorized());
api.MapFallback(() => Results.NotFound());

// Client-side routes fall back to the SPA; /api/* never does.
app.MapFallbackToFile("index.html");

app.Run();

public sealed record HealthResponse(string Status, string Version);

public partial class Program;
