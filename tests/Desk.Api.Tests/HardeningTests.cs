using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Desk.Api.Hardening;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Api.Tests;

/// <summary>README §7.3 headers and ProblemDetails, and #94: Swagger UI and OpenAPI are admin-only.</summary>
[Collection(ApiCollection.Name)]
public sealed class HardeningTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/me")]
    [InlineData("/positions")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        var res = await api.NewClient().GetAsync(path, Ct);
        Assert.Equal(SecurityHeaders.AppCsp, res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("unsafe-eval", SecurityHeaders.AppCsp);
        Assert.Contains("script-src 'self';", SecurityHeaders.AppCsp);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("same-origin", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(SecurityHeaders.PermissionsPolicy, res.Headers.GetValues("Permissions-Policy").Single());
    }

    [Fact]
    public async Task Hsts_is_sent_over_https_outside_development()
    {
        // HSTS skips localhost by design; use a real-looking host.
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://desk.example.test") });
        var res = await client.GetAsync("/health", Ct);
        Assert.StartsWith("max-age=", res.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task Unknown_api_route_is_404_problem_details_for_a_session_and_401_without()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().GetAsync("/api/nope", Ct)).StatusCode);

        var (client, _, _) = await api.SignedInAsync();
        var res = await client.GetAsync("/api/nope", Ct);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unhandled_errors_are_problem_details_without_a_stack_trace()
    {
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IInterceptor>(new ThrowingReads())));
        var res = await PostgresApiFactory.PostLoginAsync(PostgresApiFactory.NewClient(host), "someone@example.com");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync(Ct);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("ThrowingReads", body);
        Assert.DoesNotContain("   at ", body);
    }

    [Fact]
    public async Task OpenApi_and_swagger_ui_need_an_admin()
    {
        var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/openapi/v1.json", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/swagger/index.html", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/swagger", Ct)).StatusCode);

        var (viewer, _, _) = await api.SignedInAsync(Roles.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/openapi/v1.json", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/swagger/index.html", Ct)).StatusCode);

        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/openapi/v1.json", Ct)).StatusCode);
        var ui = await admin.GetAsync("/swagger/index.html", Ct);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("swagger-ui", await ui.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Swagger_ui_has_its_own_csp_and_sends_the_xsrf_header_without_eval()
    {
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);
        var ui = await admin.GetAsync("/swagger/index.html", Ct);
        Assert.Equal(SecurityHeaders.SwaggerCsp, ui.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("unsafe-eval", SecurityHeaders.SwaggerCsp);
        var html = await ui.Content.ReadAsStringAsync(Ct);
        Assert.Contains(SwaggerSetup.XsrfScriptPath, html);
        Assert.DoesNotContain("<script>", html); // no inline scripts: the CSP would block them

        var script = await admin.GetAsync(SwaggerSetup.XsrfScriptPath, Ct);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Contains("X-XSRF-TOKEN", await script.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Swagger_redirects_an_admin_to_index_html()
    {
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);
        var res = await admin.GetAsync("/swagger", Ct);
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.EndsWith("swagger/index.html", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task OpenApi_document_lists_every_api_endpoint_and_no_fallbacks()
    {
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/openapi/v1.json", Ct));
        var paths = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["/api/auth/antiforgery", "/api/auth/login", "/api/auth/logout", "/api/health/db", "/api/me", "/health"], paths);
        Assert.Equal("Credit Desk Analytics API", doc.RootElement.GetProperty("info").GetProperty("title").GetString());
        // Behind the TLS proxy a generated server would be http://; relative (none) keeps "Try it out" on https.
        if (doc.RootElement.TryGetProperty("servers", out var servers))
            Assert.All(servers.EnumerateArray(), sv => Assert.False(Uri.TryCreate(sv.GetProperty("url").GetString(), UriKind.Absolute, out _)));
        foreach (var op in doc.RootElement.GetProperty("paths").EnumerateObject().SelectMany(p => p.Value.EnumerateObject()))
        {
            Assert.True(op.Value.TryGetProperty("operationId", out _), $"{op.Name} needs WithName");
            Assert.True(op.Value.TryGetProperty("summary", out _), $"{op.Name} needs WithSummary");
            Assert.True(op.Value.TryGetProperty("tags", out _), $"{op.Name} needs WithTags");
        }
    }

    [Fact]
    public async Task Health_reports_maintenance_off_by_default_and_the_build_version()
    {
        var health = await api.NewClient().GetFromJsonAsync<HealthResponse>("/health", Ct);
        Assert.False(health!.Maintenance);
        Assert.Equal("dev", health.Version);

        await using var host = api.WithSettings(("APP_VERSION", "0123abc"));
        Assert.Equal("0123abc", (await PostgresApiFactory.NewClient(host).GetFromJsonAsync<HealthResponse>("/health", Ct))!.Version);
    }

    private sealed class ThrowingReads : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ThrowingReads");
    }
}
