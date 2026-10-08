using System.Text.Json;

namespace Desk.Data.Grid;

/// <summary>
/// The P1 grid request as the client sends it (README §6, AG Grid Infinite Row Model shapes). Nothing in here is
/// trusted: <see cref="GridQueryNormalizer"/> turns it into a <see cref="GridQuery"/> of whitelisted parts.
/// </summary>
public sealed record GridRequest(
    DateOnly? AsOf = null,
    int[]? PortfolioIds = null,
    int StartRow = 0,
    int EndRow = 200,
    string[]? Columns = null,
    SortSpec[]? SortModel = null,
    Dictionary<string, FilterSpec>? FilterModel = null,
    string? QuickFilter = null);

public sealed record SortSpec(string? ColId, string? Sort);

/// <summary>
/// One AG Grid column filter. Simple (<c>type</c> + <c>filter</c>/<c>filterTo</c>, <c>dateFrom</c>/<c>dateTo</c>, or set
/// <c>values</c>) or combined (<c>operator</c> AND/OR over <c>conditions</c>).
/// </summary>
public sealed record FilterSpec(
    string? FilterType = null,
    string? Type = null,
    JsonElement? Filter = null,
    JsonElement? FilterTo = null,
    string? DateFrom = null,
    string? DateTo = null,
    string?[]? Values = null,
    string? Operator = null,
    FilterSpec[]? Conditions = null);
