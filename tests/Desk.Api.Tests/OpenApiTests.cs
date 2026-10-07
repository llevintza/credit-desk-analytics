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

    [Fact]
    public async Task Swagger_can_be_switched_off()
    {
        var client = factory.WithWebHostBuilder(b => b.UseSetting("SWAGGER_ENABLED", "false")).CreateClient();
        var doc = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        // With Swagger off, the path falls through to the SPA fallback, never the OpenAPI JSON.
        Assert.NotEqual("application/json", doc.Content.Headers.ContentType?.MediaType);
    }
}
