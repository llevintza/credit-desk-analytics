using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Desk.Api.Tests;

public sealed class OpenApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task OpenApi_document_lists_the_api_endpoints()
    {
        var res = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/health", out _));
        Assert.True(paths.TryGetProperty("/api/me", out _));
        Assert.Equal("Credit Desk Analytics API", doc.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Swagger_ui_is_served_not_the_spa()
    {
        var res = await factory.CreateClient().GetAsync("/swagger/index.html", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("swagger-ui", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Development", "false")] // explicitly off
    [InlineData("Production", null)]     // off by default outside Development
    public async Task Swagger_is_off_when_disabled_or_in_production_by_default(string environment, string? setting)
    {
        var client = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            if (setting is not null) b.UseSetting("SWAGGER_ENABLED", setting);
        }).CreateClient();
        foreach (var path in new[] { "/openapi/v1.json", "/swagger/index.html" })
        {
            var res = await client.GetAsync(path, TestContext.Current.CancellationToken);
            var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            // Not served at all: the SPA fallback skips file-like paths, so these are 404 (no OpenAPI JSON, no UI).
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.DoesNotContain("\"openapi\"", body);
            Assert.DoesNotContain("swagger-ui", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Swagger_can_be_enabled_in_production()
    {
        var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Production").UseSetting("SWAGGER_ENABLED", "true")).CreateClient();
        var res = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
    }
}
