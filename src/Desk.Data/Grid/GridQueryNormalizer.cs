using System.Globalization;
using System.Text.Json;
using Desk.Data.Catalog;

namespace Desk.Data.Grid;

/// <summary>
/// Whitelists a <see cref="GridRequest"/> against the column catalog (README §6 P1 server rules, AGENTS.md SQL safety).
/// Unknown or malformed parts are dropped, never echoed into SQL and never an error: a bad sort id, an injection
/// attempt in a filter key or a filter whose value doesn't parse simply has no effect.
/// </summary>
public sealed class GridQueryNormalizer
{
    public const int MaxBlockRows = 500;
    public const int MaxColumns = 250;
    public const int MaxSortColumns = 5;
    public const int MaxFilters = 50;
    public const int MaxSetValues = 1000;
    public const int MaxQuickTokens = 5;
    public const int MaxTextLength = 200;
    public const string RowIdColumn = "position_id";

    private readonly Dictionary<string, ColumnDef> _byName;
    private readonly ColumnDef _rowId;

    public GridQueryNormalizer(IEnumerable<ColumnDef> catalog)
    {
        _byName = catalog.ToDictionary(c => c.Name, StringComparer.Ordinal);
        _rowId = _byName[RowIdColumn];
    }

    /// <param name="asOf">The as-of date to read (already validated against the available dates).</param>
    /// <param name="entitled">Portfolios the caller may see; requested ids outside it are dropped.</param>
    public GridQuery Normalize(GridRequest request, DateOnly asOf, IReadOnlyCollection<int> entitled)
    {
        var start = Math.Max(0, request.StartRow);
        var end = Math.Max(start, request.EndRow);
        var limit = Math.Min(end - start, MaxBlockRows);

        var portfolios = (request.PortfolioIds is { Length: > 0 } ids ? ids.Where(entitled.Contains) : entitled)
            .Distinct().Order().ToArray();

        return new GridQuery(asOf, portfolios, start, limit, Columns(request.Columns), Sort(request.SortModel),
            Filters(request.FilterModel), QuickTokens(request.QuickFilter));
    }

    private List<ColumnDef> Columns(string[]? requested)
    {
        // position_id first (the row id), then the requested catalog columns in order, without duplicates.
        var columns = new List<ColumnDef> { _rowId };
        foreach (var name in (requested ?? []).OfType<string>())
            if (_byName.TryGetValue(name, out var col) && !columns.Contains(col))
                columns.Add(col);
        return columns.Take(MaxColumns).ToList();
    }

    private List<GridSort> Sort(SortSpec[]? model)
    {
        var sort = new List<GridSort>();
        foreach (var s in model ?? [])
        {
            if (sort.Count >= MaxSortColumns) break;
            if (s?.ColId is null || !_byName.TryGetValue(s.ColId, out var col) || sort.Any(x => x.Column == col))
                continue;
            if (s.Sort is "asc" or "desc")
                sort.Add(new GridSort(col, s.Sort == "desc"));
        }
        // Deterministic tie-breaker: without it OFFSET paging can repeat or skip rows with equal sort keys. It runs
        // in the first key's direction, so one btree (as_of_date, key, position_id) serves both directions.
        if (!sort.Any(x => x.Column == _rowId))
            sort.Add(new GridSort(_rowId, sort.Count > 0 && sort[0].Descending));
        return sort;
    }

    private List<GridFilter> Filters(Dictionary<string, FilterSpec>? model)
    {
        var filters = new List<GridFilter>();
        foreach (var (key, spec) in (model ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (filters.Count >= MaxFilters) break;
            if (spec is null || !_byName.TryGetValue(key, out var col)) continue;

            if (spec.Conditions is { Length: > 0 } parts)
            {
                var conditions = parts.Take(2).Select(p => Condition(col, p, spec.FilterType)).OfType<GridCondition>().ToList();
                if (conditions.Count == parts.Take(2).Count())
                    filters.Add(new GridFilter(col, string.Equals(spec.Operator, "OR", StringComparison.OrdinalIgnoreCase), conditions));
            }
            else if (Condition(col, spec, spec.FilterType) is { } condition)
            {
                filters.Add(new GridFilter(col, false, [condition]));
            }
        }
        return filters;
    }

    private static GridCondition? Condition(ColumnDef col, FilterSpec spec, string? parentType)
    {
        var filterType = spec.FilterType ?? parentType;
        return filterType switch
        {
            "number" when IsNumeric(col.Kind) => NumberCondition(spec),
            "text" when col.Kind == ColumnKind.Text => TextCondition(spec),
            "date" when col.Kind == ColumnKind.Date => DateCondition(spec),
            "set" => SetCondition(spec),
            _ => null,
        };
    }

    internal static bool IsNumeric(ColumnKind kind) => kind is ColumnKind.Key or ColumnKind.Money or ColumnKind.Price
        or ColumnKind.Bp or ColumnKind.Pct or ColumnKind.Ratio or ColumnKind.Count;

    private static GridCondition? NumberCondition(FilterSpec spec)
    {
        var op = spec.Type switch
        {
            "equals" => FilterOp.Equals,
            "notEqual" => FilterOp.NotEqual,
            "lessThan" => FilterOp.LessThan,
            "lessThanOrEqual" => FilterOp.LessThanOrEqual,
            "greaterThan" => FilterOp.GreaterThan,
            "greaterThanOrEqual" => FilterOp.GreaterThanOrEqual,
            "inRange" => FilterOp.InRange,
            "blank" => FilterOp.Blank,
            "notBlank" => FilterOp.NotBlank,
            _ => (FilterOp?)null,
        };
        if (op is null) return null;
        if (op is FilterOp.Blank or FilterOp.NotBlank) return new GridCondition(FilterKind.Number, op.Value);

        var from = Number(spec.Filter);
        if (from is null) return null;
        if (op != FilterOp.InRange) return new GridCondition(FilterKind.Number, op.Value, from);
        var to = Number(spec.FilterTo);
        return to is null ? null : new GridCondition(FilterKind.Number, op.Value, from, to);
    }

    private static double? Number(JsonElement? e)
    {
        double v;
        if (e is { ValueKind: JsonValueKind.Number } n) v = n.GetDouble();
        else if (e is { ValueKind: JsonValueKind.String } s && double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) v = parsed;
        else return null;
        return double.IsFinite(v) ? v : null;
    }

    private static GridCondition? TextCondition(FilterSpec spec)
    {
        var op = spec.Type switch
        {
            "contains" => FilterOp.Contains,
            "notContains" => FilterOp.NotContains,
            "equals" => FilterOp.Equals,
            "notEqual" => FilterOp.NotEqual,
            "startsWith" => FilterOp.StartsWith,
            "endsWith" => FilterOp.EndsWith,
            "blank" => FilterOp.Blank,
            "notBlank" => FilterOp.NotBlank,
            _ => (FilterOp?)null,
        };
        if (op is null) return null;
        if (op is FilterOp.Blank or FilterOp.NotBlank) return new GridCondition(FilterKind.Text, op.Value);
        return spec.Filter is { ValueKind: JsonValueKind.String } s && s.GetString() is { Length: > 0 and <= MaxTextLength } text
            ? new GridCondition(FilterKind.Text, op.Value, text)
            : null;
    }

    private static GridCondition? DateCondition(FilterSpec spec)
    {
        var op = spec.Type switch
        {
            "equals" => FilterOp.Equals,
            "notEqual" => FilterOp.NotEqual,
            "lessThan" => FilterOp.LessThan,
            "greaterThan" => FilterOp.GreaterThan,
            "inRange" => FilterOp.InRange,
            "blank" => FilterOp.Blank,
            "notBlank" => FilterOp.NotBlank,
            _ => (FilterOp?)null,
        };
        if (op is null) return null;
        if (op is FilterOp.Blank or FilterOp.NotBlank) return new GridCondition(FilterKind.Date, op.Value);

        var from = Date(spec.DateFrom);
        if (from is null) return null;
        if (op != FilterOp.InRange) return new GridCondition(FilterKind.Date, op.Value, from);
        var to = Date(spec.DateTo);
        return to is null ? null : new GridCondition(FilterKind.Date, op.Value, from, to);
    }

    /// <summary>AG Grid sends "yyyy-MM-dd HH:mm:ss"; only the date part counts.</summary>
    private static DateOnly? Date(string? s) =>
        s is { Length: >= 10 } && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    private static GridCondition? SetCondition(FilterSpec spec)
    {
        if (spec.Values is null || spec.Values.Length > MaxSetValues) return null;
        var values = spec.Values.Where(v => v is null || v.Length <= MaxTextLength).Distinct().Order(StringComparer.Ordinal).ToArray();
        return new GridCondition(FilterKind.Set, FilterOp.In, Values: values);
    }

    private static List<string> QuickTokens(string? quick) =>
        string.IsNullOrWhiteSpace(quick)
            ? []
            : quick.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length <= 64)
                .Select(t => t.ToLowerInvariant())
                .Distinct()
                .Take(MaxQuickTokens)
                .ToList();
}
