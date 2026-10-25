using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Desk.Mcp;

/// <summary>
/// The meta tools (ADR-0031 §4, #554). Each maps to one existing endpoint and is read-only; the API applies
/// entitlements, limits, caching and audit. Names stay on the allow side of <c>decideMcp</c> (DeskToolNameGuardTests).
/// </summary>
[McpServerToolType]
internal sealed class DeskTools(DeskApiClient api)
{
    [McpServerTool(Name = "get_as_of_dates", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("The as-of dates loaded on the desk, newest first, and the latest one. Not portfolio-owned.")]
    public async Task<string> GetAsOfDatesAsync(CancellationToken ct)
    {
        var body = await api.GetJsonAsync("/api/meta/as-of", ct);
        var rows = body.GetProperty("dates").EnumerateArray().Select(d => new JsonArray(JsonValue.Create(d.GetString()))).ToList();
        return TableResult.Build(["as_of_date"], rows, TableResult.MaxRows,
            new JsonObject { ["latest"] = body.GetProperty("latest").GetString() });
    }

    [McpServerTool(Name = "list_columns", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("The column catalog: name, group, type and aggregation of every column the positions grid can show.")]
    public async Task<string> ListColumnsAsync(CancellationToken ct)
    {
        var body = await api.GetJsonAsync("/api/meta/columns", ct);
        var rows = body.EnumerateArray().Select(c => Row(c, "name", "group", "kind", "aggregation")).ToList();
        return TableResult.Build(["name", "group", "type", "aggregation"], rows, TableResult.MaxRows);
    }

    [McpServerTool(Name = "list_portfolios", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("The portfolios the signed-in account is entitled to, with their fund. Other portfolios are never listed.")]
    public async Task<string> ListPortfoliosAsync(CancellationToken ct)
    {
        var body = await api.GetJsonAsync("/api/meta/portfolios", ct);
        var rows = body.EnumerateArray().Select(p => Row(p, "portfolioId", "name", "fundId", "fundName")).ToList();
        return TableResult.Build(["portfolio_id", "name", "fund_id", "fund_name"], rows, TableResult.MaxRows);
    }

    private static JsonArray Row(JsonElement item, params string[] keys) =>
        new([.. keys.Select(k => item.TryGetProperty(k, out var v) ? JsonNode.Parse(v.GetRawText()) : null)]);
}
