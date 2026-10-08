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
    public string SummaryKey => Key(new
    {
        AsOf, PortfolioIds,
        Aggregated = Columns.Where(c => c.Aggregation != Catalog.Aggregation.None).Select(c => c.Name).Order(StringComparer.Ordinal),
        Filters = Filters.Select(KeyOf), QuickTokens,
    });

    /// <summary>
    /// Everything that changes the result, as JSON: user text is escaped by the serializer, so two different
    /// queries can never produce the same key (a shared cache must not be poisonable through filter values).
    /// </summary>
    public string CanonicalKey => Key(new
    {
        AsOf, PortfolioIds, Offset, Limit,
        Columns = Columns.Select(c => c.Name),
        Sort = Sort.Select(s => new { s.Column.Name, s.Descending }),
        Filters = Filters.Select(KeyOf), QuickTokens,
    });

    private static object KeyOf(GridFilter f) => new
    {
        f.Column.Name, f.Or,
        Conditions = f.Conditions.Select(c => new { Kind = c.Kind.ToString(), Op = c.Op.ToString(), c.Value, c.ValueTo, c.Values }),
    };

    private static string Key(object value) => System.Text.Json.JsonSerializer.Serialize(value);
}

public sealed record GridSort(ColumnDef Column, bool Descending);

/// <summary>One column's filter: one or more conditions joined by AND/OR.</summary>
public sealed record GridFilter(ColumnDef Column, bool Or, IReadOnlyList<GridCondition> Conditions);

/// <summary>
/// A typed condition. <see cref="Value"/> / <see cref="ValueTo"/> are decimal (money), double (other numbers),
/// string or DateOnly by filter kind;
/// <see cref="Values"/> is the set filter's list (null entries mean "(Blanks)").
/// </summary>
public sealed record GridCondition(FilterKind Kind, FilterOp Op, object? Value = null, object? ValueTo = null, IReadOnlyList<string?>? Values = null);

/// <summary>
/// A request whose filter can't be applied as asked (too many filters, conditions or set values, text too long, a
/// money value out of range, or a combined filter with an unusable part). Dropping it would widen the result past
/// what the user selected, so the API answers 400 instead.
/// </summary>
public sealed class GridRequestException(string message) : Exception(message);

public enum FilterKind { Number, Text, Date, Set }

public enum FilterOp
{
    Equals, NotEqual, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual, InRange,
    Contains, NotContains, StartsWith, EndsWith, Blank, NotBlank, In,
}
