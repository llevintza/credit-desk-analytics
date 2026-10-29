using System.Text;
using System.Text.Json.Nodes;

namespace Desk.Mcp;

/// <summary>
/// A tool result as one <c>columns</c> header plus row arrays (ADR-0031 §5), capped at <see cref="MaxRows"/> rows and
/// <see cref="MaxBytes"/> bytes of UTF-8. A cut is flagged with <c>truncated</c>, <c>truncated_by</c>,
/// <c>rows_returned</c> and <c>rows_matched</c>.
/// </summary>
internal static class TableResult
{
    public const int DefaultRows = 100;
    public const int MaxRows = 500;
    public const int MaxBytes = 65_536;

    public static string Build(string[] columns, IReadOnlyList<JsonArray> rows, int limit, JsonObject? extra = null, int maxBytes = MaxBytes)
    {
        var take = Math.Min(rows.Count, Math.Clamp(limit, 1, MaxRows));
        var by = take < rows.Count ? "rows" : null;
        var text = Render(columns, rows, take, by, extra);
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

        // The most rows that fit: Render grows with the row count, so search it.
        int lo = 0, hi = take - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Encoding.UTF8.GetByteCount(Render(columns, rows, mid, "bytes", extra)) <= maxBytes) lo = mid;
            else hi = mid - 1;
        }
        return Render(columns, rows, lo, "bytes", extra);
    }

    private static string Render(string[] columns, IReadOnlyList<JsonArray> rows, int take, string? truncatedBy, JsonObject? extra)
    {
        var o = new JsonObject();
        foreach (var (key, value) in extra ?? []) o[key] = value?.DeepClone();
        o["columns"] = new JsonArray([.. columns.Select(c => JsonValue.Create(c))]);
        o["rows"] = new JsonArray([.. rows.Take(take).Select(r => r.DeepClone())]);
        o["rows_returned"] = take;
        o["rows_matched"] = rows.Count;
        o["truncated"] = truncatedBy is not null;
        if (truncatedBy is not null) o["truncated_by"] = truncatedBy;
        return o.ToJsonString();
    }
}
