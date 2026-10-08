using Desk.Data.Catalog;

namespace Desk.Data.Grid;

/// <summary>
/// A grid request after whitelisting: every column is a catalog <see cref="ColumnDef"/>, every value is typed and
/// will become a parameter. Two requests that mean the same thing normalize to equal queries, so
/// <see cref="CanonicalKey"/> is a safe cache key (junk the client added is gone before the key is built).
/// </summary>
public sealed record GridQuery(
    DateOnly AsOf,
    int[] PortfolioIds,
    int Offset,
    int Limit,
    IReadOnlyList<ColumnDef> Columns,
    IReadOnlyList<GridSort> Sort,
    IReadOnlyList<GridFilter> Filters,
    IReadOnlyList<string> QuickTokens)
{
    /// <summary>
    /// What the totals depend on: the filter and the aggregated columns, not paging or sort. Every block of one
    /// filtered view shares it, so the summary is computed once per view.
    /// </summary>
    public string SummaryKey => string.Join('|',
        AsOf.ToString("yyyy-MM-dd"),
        string.Join(',', PortfolioIds),
        string.Join(',', Columns.Where(c => c.Aggregation != Catalog.Aggregation.None).Select(c => c.Name).Order(StringComparer.Ordinal)),
        string.Join(';', Filters.Select(f => f.Canonical)),
        string.Join(' ', QuickTokens));

    /// <summary>Stable text form of everything that changes the result.</summary>
    public string CanonicalKey => string.Join('|',
        AsOf.ToString("yyyy-MM-dd"),
        string.Join(',', PortfolioIds),
        Offset, Limit,
        string.Join(',', Columns.Select(c => c.Name)),
        string.Join(',', Sort.Select(s => $"{s.Column.Name}:{(s.Descending ? "d" : "a")}")),
        string.Join(';', Filters.Select(f => f.Canonical)),
        string.Join(' ', QuickTokens));
}

public sealed record GridSort(ColumnDef Column, bool Descending);

/// <summary>One column's filter: one or two conditions joined by AND/OR.</summary>
public sealed record GridFilter(ColumnDef Column, bool Or, IReadOnlyList<GridCondition> Conditions)
{
    public string Canonical => $"{Column.Name}{(Or ? "|or" : "")}:{string.Join('&', Conditions.Select(c => c.Canonical))}";
}

/// <summary>
/// A typed condition. <see cref="Value"/> / <see cref="ValueTo"/> are double, string or DateOnly by filter kind;
/// <see cref="Values"/> is the set filter's list (null entries mean "(Blanks)").
/// </summary>
public sealed record GridCondition(FilterKind Kind, FilterOp Op, object? Value = null, object? ValueTo = null, IReadOnlyList<string?>? Values = null)
{
    public string Canonical => $"{Kind}.{Op}({Format(Value)},{Format(ValueTo)},{(Values is null ? "" : string.Join('\u001f', Values.Select(v => v ?? "\u0000")))})";

    private static string Format(object? v) => v switch
    {
        null => "",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd"),
        _ => v.ToString()!,
    };
}

public enum FilterKind { Number, Text, Date, Set }

public enum FilterOp
{
    Equals, NotEqual, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual, InRange,
    Contains, NotContains, StartsWith, EndsWith, Blank, NotBlank, In,
}
