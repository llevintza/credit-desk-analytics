using System.Text.Json;
using Desk.Data.Auth;

namespace Desk.Api.Tests;

/// <summary>
/// ADR-0019 (#254, #307): every operation's response set is pinned exactly. An endpoint declares what its handler
/// returns; <c>PipelineResponses</c> adds the shared pipeline's statuses (binding and antiforgery 400, 401, 403, 415,
/// 429, maintenance 503) from the endpoint's metadata. A new or changed operation fails here until it's pinned.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OpenApiResponsesTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>"METHOD path" → the exact status set the document declares.</summary>
    private static readonly Dictionary<string, string> Pinned = new()
    {
        ["GET /health"] = "200",
        ["GET /api/health/db"] = "200,401,403,429,503",
        ["POST /api/auth/login"] = "200,400,401,415,429,503",
        ["POST /api/auth/logout"] = "204,400,401,429,503",
        ["GET /api/auth/antiforgery"] = "204,401,429,503",
        ["GET /api/me"] = "200,401,429,503",
        ["GET /api/meta/as-of"] = "200,401,429,503",
        ["GET /api/meta/columns"] = "200,401,429,503",
        ["GET /api/meta/portfolios"] = "200,401,429,503",
        ["GET /api/presets/{page}"] = "200,401,404,429,503",
        ["PUT /api/presets/{page}"] = "204,400,401,404,409,415,429,503",
        ["DELETE /api/presets/{page}"] = "204,400,401,404,429,503",
        ["POST /api/positions/query"] = "200,304,400,401,415,429,503",
        ["POST /api/positions/export"] = "200,400,401,415,429,503",
        ["GET /api/funds/{fundId}/performance"] = "200,304,400,401,404,429,503",
        ["GET /api/insights/{source}"] = "200,304,400,401,404,429,503",
        ["POST /api/admin/cache/clear"] = "204,400,401,403,429,503",
    };

    public static TheoryData<string> PinnedOperations => [.. Pinned.Keys];

    private async Task<Dictionary<string, JsonElement>> OperationsAsync()
    {
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/openapi/v1.json", Ct));
        return doc.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(op => ($"{op.Name.ToUpperInvariant()} {path.Name}", op.Value.Clone())))
            .ToDictionary(op => op.Item1, op => op.Item2);
    }

    [Fact]
    public async Task Every_operation_in_the_document_is_pinned_and_every_pin_exists()
    {
        var operations = await OperationsAsync();
        Assert.Equal(Pinned.Keys.Order(), operations.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(PinnedOperations))]
    public async Task Each_operation_declares_exactly_its_pinned_statuses(string operation)
    {
        var responses = (await OperationsAsync())[operation].GetProperty("responses");
        Assert.Equal(Pinned[operation].Split(',').Order(), responses.EnumerateObject().Select(r => r.Name).Order());
    }

    [Fact]
    public async Task Every_error_response_is_problem_json_with_the_problem_details_schema()
    {
        var errors = (await OperationsAsync())
            .SelectMany(op => op.Value.GetProperty("responses").EnumerateObject().Select(r => (Op: op.Key, Status: r.Name, Response: r.Value)))
            .Where(r => r.Status[0] is '4' or '5')
            .ToList();

        Assert.NotEmpty(errors);
        Assert.All(errors, r =>
        {
            var content = r.Response.GetProperty("content").EnumerateObject().ToList();
            var media = Assert.Single(content);
            Assert.Equal("application/problem+json", media.Name);
            Assert.Equal("#/components/schemas/ProblemDetails", media.Value.GetProperty("schema").GetProperty("$ref").GetString());
        });
    }
}
