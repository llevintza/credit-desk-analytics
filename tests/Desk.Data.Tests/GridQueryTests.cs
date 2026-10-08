using System.Text.Json;
using Desk.Data.Catalog;
using Desk.Data.Grid;

namespace Desk.Data.Tests;

/// <summary>README §11 unit tests: GridQueryBuilder whitelist, parameters and SQL snapshots.</summary>
public sealed class GridQueryTests
{
    private static readonly GridQueryNormalizer Normalizer = new(ColumnCatalog.PositionSnapshot);
    private static readonly DateOnly AsOf = new(2026, 10, 6);
    private static readonly int[] Entitled = [1, 2, 3];

    private static GridQuery Normalize(GridRequest r) => Normalizer.Normalize(r, AsOf, Entitled);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static GridSql Sql(GridRequest r) => GridSqlBuilder.Build(Normalize(r), ColumnCatalog.PositionSnapshot);

    [Fact]
    public void Columns_are_whitelisted_deduplicated_and_led_by_the_row_id()
    {
        var q = Normalize(new GridRequest(Columns: ["deal_name", "market_value", "deal_name", null!, "nope", "x; DROP TABLE core.deal --", "position_id"]));
        Assert.Equal(["position_id", "deal_name", "market_value"], q.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Columns_are_capped_and_blocks_are_clamped()
    {
        var many = Enumerable.Repeat(ColumnCatalog.PositionSnapshot.Select(c => c.Name), 3).SelectMany(x => x).ToArray();
        var q = Normalize(new GridRequest(StartRow: -5, EndRow: 100_000, Columns: many));
        Assert.True(q.Columns.Count <= GridQueryNormalizer.MaxColumns);
        Assert.Equal(0, q.Offset);
        Assert.Equal(GridQueryNormalizer.MaxBlockRows, q.Limit);

        var inverted = Normalize(new GridRequest(StartRow: 400, EndRow: 100));
        Assert.Equal(400, inverted.Offset);
        Assert.Equal(0, inverted.Limit);
    }

    [Fact]
    public void Portfolios_are_limited_to_the_entitlement()
    {
        Assert.Equal([1, 2, 3], Normalize(new GridRequest()).PortfolioIds);
        Assert.Equal([1, 3], Normalize(new GridRequest(PortfolioIds: [3, 99, 1, 3])).PortfolioIds);
        Assert.Equal([1, 2, 3], Normalize(new GridRequest(PortfolioIds: [])).PortfolioIds);
        Assert.Empty(Normalize(new GridRequest(PortfolioIds: [99])).PortfolioIds);
    }

    [Fact]
    public void Unknown_or_malicious_sort_ids_are_dropped_and_position_id_breaks_ties_in_the_first_keys_direction()
    {
        var q = Normalize(new GridRequest(SortModel:
        [
            new("market_value; DROP TABLE x", "desc"), new("spread_bp", "desc"), new("dv01", "sideways"),
            new("spread_bp", "asc"), new(null, "asc"), null!, new("deal_name", "asc"),
        ]));
        Assert.Equal(["spread_bp:d", "deal_name:a", "position_id:d"], q.Sort.Select(s => $"{s.Column.Name}:{(s.Descending ? "d" : "a")}"));

        Assert.Equal(["position_id:a"], Normalize(new GridRequest()).Sort.Select(s => $"{s.Column.Name}:{(s.Descending ? "d" : "a")}"));
        // Sorting by the row id itself is honoured, not duplicated.
        Assert.Equal(["position_id:d"], Normalize(new GridRequest(SortModel: [new("position_id", "desc")])).Sort.Select(s => $"{s.Column.Name}:{(s.Descending ? "d" : "a")}"));
    }

    [Fact]
    public void Sort_columns_are_capped()
    {
        var names = new[] { "price", "yield", "spread_bp", "oas_bp", "dm_bp", "dv01", "cs01" };
        var q = Normalize(new GridRequest(SortModel: names.Select(n => new SortSpec(n, "asc")).ToArray()));
        Assert.Equal(GridQueryNormalizer.MaxSortColumns + 1, q.Sort.Count);
    }

    [Fact]
    public void Filter_keys_must_be_catalog_columns_and_the_filter_must_fit_the_column_kind()
    {
        var q = Normalize(new GridRequest(FilterModel: new()
        {
            ["deal_name; DROP TABLE core.deal --"] = new("text", "contains", Json("\"x\"")),
            ["spread_bp"] = new("text", "contains", Json("\"x\"")),          // text filter on a number column
            ["deal_name"] = new("number", "equals", Json("5")),              // number filter on a text column
            ["window_start"] = new("number", "equals", Json("5")),           // number filter on a date column
            ["dv01"] = new("number", "lessThan", Json("\"not a number\"")),
            ["cs01"] = new("number", "explode", Json("1")),
            ["price"] = new("number", "greaterThan", Json("\"98.5\"")),       // numeric string is fine
            ["yield"] = new("number", "inRange", Json("1")),                  // inRange without filterTo
            ["oas_bp"] = new("number", "equals", Json("1e400")),              // not finite
            ["cusip"] = new("text", "contains", Json("\"\"")),                // empty text
            ["sector"] = new("bogus"),
            ["class"] = null!,
        }));
        Assert.Equal("price", Assert.Single(q.Filters).Column.Name);
    }

    [Fact]
    public void Every_value_is_a_parameter_and_identifiers_are_quoted_catalog_names()
    {
        var evil = "'; DROP TABLE core.deal; --";
        var sql = Sql(new GridRequest(
            Columns: ["deal_name", "class"],
            FilterModel: new()
            {
                ["deal_name"] = new("text", "contains", Json(JsonSerializer.Serialize(evil))),
                ["sector"] = new("set", Values: [evil, null]),
                ["spread_bp"] = new("number", "greaterThan", Json("250")),
            },
            QuickFilter: evil));

        Assert.DoesNotContain("DROP", sql.Sql);
        Assert.Contains(sql.Parameters, p => p.Value is string s && s.Contains("DROP"));
        Assert.Contains("\"class\"", sql.Sql);
        Assert.Contains(sql.Parameters, p => p.Value is double d && d == 250);
    }

    [Fact]
    public void Sql_snapshot_for_a_typical_risk_request()
    {
        var sql = Sql(new GridRequest(
            StartRow: 200, EndRow: 400,
            Columns: ["deal_name", "market_value", "spread_bp", "price_source"],
            SortModel: [new("market_value", "desc")],
            FilterModel: new()
            {
                ["sector"] = new("set", Values: ["RMBS", "CLO"]),
                ["spread_bp"] = new("number", "greaterThan", Json("250")),
                ["deal_name"] = new("text", "contains", Json("\"2024\"")),
            },
            QuickFilter: "  CLO  2024 clo "));

        var expected =
            "SELECT \"position_id\", \"deal_name\", \"market_value\", \"spread_bp\", \"price_source\" FROM core.position_snapshot " +
            "WHERE as_of_date = @as_of AND portfolio_id = ANY(@portfolios) AND \"deal_name\" ILIKE @p4 AND \"sector\" = ANY(@p5) " +
            "AND \"spread_bp\" > @p6 AND " + Haystack() + " ILIKE @p7 AND " + Haystack() + " ILIKE @p8 " +
            "ORDER BY \"market_value\" DESC, \"position_id\" DESC OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;\n" +
            "SELECT COUNT(*)::int AS row_count, SUM(\"market_value\") AS \"market_value\", " +
            "SUM(\"spread_bp\" * w.weight_f8) / NULLIF(SUM(w.weight_f8) FILTER (WHERE \"spread_bp\" IS NOT NULL), 0) AS \"spread_bp\" " +
            "FROM core.position_snapshot CROSS JOIN LATERAL (SELECT abs(market_value)::float8 AS weight_f8 OFFSET 0) w WHERE as_of_date = @as_of AND portfolio_id = ANY(@portfolios) AND \"deal_name\" ILIKE @p4 AND \"sector\" = ANY(@p5) " +
            "AND \"spread_bp\" > @p6 AND " + Haystack() + " ILIKE @p7 AND " + Haystack() + " ILIKE @p8;";
        Assert.Equal(expected, sql.Sql); // the WHERE text and its parameters are shared by both statements

        var p = sql.Parameters.ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal(AsOf, p["as_of"]);
        Assert.Equal(new[] { 1, 2, 3 }, p["portfolios"]);
        Assert.Equal(200, p["offset"]);
        Assert.Equal(200, p["limit"]);
        Assert.Equal("%2024%", p["p4"]);
        Assert.Equal(new[] { "CLO", "RMBS" }, p["p5"]); // set values are sorted: same filter, same cache key
        Assert.Equal(250.0, p["p6"]);
        Assert.Equal("%clo%", p["p7"]);                // quick filter: lower-cased, de-duplicated tokens
        Assert.Equal("%2024%", p["p8"]);
    }

    private static string Haystack() =>
        $"concat_ws(' ', {string.Join(", ", ColumnCatalog.PositionSnapshot.Where(c => c.Kind == ColumnKind.Text).Select(c => $"\"{c.Name}\""))})";

    public static TheoryData<string, string, string?, string> NumberOps => new()
    {
        { "equals", "5", null, "\"dv01\" = @p4" },
        { "notEqual", "5", null, "(\"dv01\" IS NULL OR \"dv01\" <> @p4)" },
        { "lessThan", "5", null, "\"dv01\" < @p4" },
        { "lessThanOrEqual", "5", null, "\"dv01\" <= @p4" },
        { "greaterThan", "5", null, "\"dv01\" > @p4" },
        { "greaterThanOrEqual", "5", null, "\"dv01\" >= @p4" },
        { "inRange", "5", "9", "(\"dv01\" > @p4 AND \"dv01\" < @p5)" },
        { "blank", "0", null, "\"dv01\" IS NULL" },
        { "notBlank", "0", null, "\"dv01\" IS NOT NULL" },
    };

    [Theory]
    [MemberData(nameof(NumberOps))]
    public void Number_filters(string type, string value, string? to, string expected)
    {
        var sql = Sql(new GridRequest(FilterModel: new() { ["dv01"] = new("number", type, Json(value), to is null ? null : Json(to)) }));
        Assert.Contains(expected, sql.Sql);
    }

    public static TheoryData<string, string, string> TextOps => new()
    {
        { "contains", "\"deal_name\" ILIKE @p4", "%a\\%b\\_c\\\\d%" },
        { "notContains", "(\"deal_name\" IS NULL OR \"deal_name\" NOT ILIKE @p4)", "%a\\%b\\_c\\\\d%" },
        { "startsWith", "\"deal_name\" ILIKE @p4", "a\\%b\\_c\\\\d%" },
        { "endsWith", "\"deal_name\" ILIKE @p4", "%a\\%b\\_c\\\\d" },
        { "equals", "\"deal_name\" ILIKE @p4", "a\\%b\\_c\\\\d" },
        { "notEqual", "(\"deal_name\" IS NULL OR \"deal_name\" NOT ILIKE @p4)", "a\\%b\\_c\\\\d" },
    };

    [Theory]
    [MemberData(nameof(TextOps))]
    public void Text_filters_escape_like_wildcards(string type, string expected, string parameter)
    {
        var sql = Sql(new GridRequest(FilterModel: new() { ["deal_name"] = new("text", type, Json(JsonSerializer.Serialize(@"a%b_c\d"))) }));
        Assert.Contains(expected, sql.Sql);
        Assert.Equal(parameter, sql.Parameters.Single(p => p.Key == "p4").Value);
    }

    [Theory]
    [InlineData("blank", "\"deal_name\" IS NULL")]
    [InlineData("notBlank", "\"deal_name\" IS NOT NULL")]
    public void Text_blank_filters_need_no_value(string type, string expected) =>
        Assert.Contains(expected, Sql(new GridRequest(FilterModel: new() { ["deal_name"] = new("text", type) })).Sql);

    [Fact]
    public void Text_filters_reject_unknown_types_non_strings_and_overlong_values()
    {
        foreach (var spec in new FilterSpec[]
        {
            new("text", "regex", Json("\"x\"")),
            new("text", "contains", Json("5")),
            new("text", "contains", Json(JsonSerializer.Serialize(new string('x', GridQueryNormalizer.MaxTextLength + 1)))),
        })
            Assert.Empty(Normalize(new GridRequest(FilterModel: new() { ["deal_name"] = spec })).Filters);
    }

    [Theory]
    [InlineData("equals", "2026-11-01 00:00:00", null, "\"next_pay_date\" = @p4")]
    [InlineData("notEqual", "2026-11-01", null, "(\"next_pay_date\" IS NULL OR \"next_pay_date\" <> @p4)")]
    [InlineData("lessThan", "2026-11-01", null, "\"next_pay_date\" < @p4")]
    [InlineData("greaterThan", "2026-11-01", null, "\"next_pay_date\" > @p4")]
    [InlineData("inRange", "2026-11-01", "2026-12-01", "(\"next_pay_date\" > @p4 AND \"next_pay_date\" < @p5)")]
    [InlineData("blank", null, null, "\"next_pay_date\" IS NULL")]
    [InlineData("notBlank", null, null, "\"next_pay_date\" IS NOT NULL")]
    public void Date_filters(string type, string? from, string? to, string expected)
    {
        var sql = Sql(new GridRequest(FilterModel: new() { ["next_pay_date"] = new("date", type, DateFrom: from, DateTo: to) }));
        Assert.Contains(expected, sql.Sql);
        if (from is not null) Assert.Equal(new DateOnly(2026, 11, 1), sql.Parameters.Single(p => p.Key == "p4").Value);
    }

    [Theory]
    [InlineData("equals", "11/01/2026", null)]
    [InlineData("equals", null, null)]
    [InlineData("inRange", "2026-11-01", null)]
    [InlineData("inRange", "2026-11-01", "soon")]
    [InlineData("around", "2026-11-01", null)]
    public void Malformed_date_filters_are_dropped(string type, string? from, string? to) =>
        Assert.Empty(Normalize(new GridRequest(FilterModel: new() { ["next_pay_date"] = new("date", type, DateFrom: from, DateTo: to) })).Filters);

    [Fact]
    public void Set_filters_cover_text_non_text_blanks_and_empty_selection()
    {
        Assert.Contains("\"sector\" = ANY(@p4)", Sql(new GridRequest(FilterModel: new() { ["sector"] = new("set", Values: ["CLO"]) })).Sql);
        Assert.Contains("\"vintage\"::text = ANY(@p4)", Sql(new GridRequest(FilterModel: new() { ["vintage"] = new("set", Values: ["2021"]) })).Sql);
        Assert.Contains("(\"class\" = ANY(@p4) OR \"class\" IS NULL)", Sql(new GridRequest(FilterModel: new() { ["class"] = new("set", Values: ["A1", null]) })).Sql);
        Assert.Contains("\"class\" IS NULL", Sql(new GridRequest(FilterModel: new() { ["class"] = new("set", Values: [null]) })).Sql);
        Assert.Contains(" AND FALSE", Sql(new GridRequest(FilterModel: new() { ["class"] = new("set", Values: []) })).Sql);
        Assert.Empty(Normalize(new GridRequest(FilterModel: new() { ["class"] = new("set") })).Filters);
        Assert.Throws<GridRequestException>(() => Normalize(new GridRequest(FilterModel: new() { ["class"] = new("set", Values: new string?[GridQueryNormalizer.MaxSetValues + 1]) })));
        // Long values are kept, not silently removed: they're parameters.
        var longValue = new string('x', 500);
        Assert.Equal([longValue], Normalize(new GridRequest(FilterModel: new() { ["class"] = new("set", Values: [longValue]) })).Filters[0].Conditions[0].Values);
    }

    [Fact]
    public void Combined_conditions_join_with_the_operator_and_drop_if_any_part_is_invalid()
    {
        var or = Sql(new GridRequest(FilterModel: new()
        {
            ["dv01"] = new("number", Operator: "OR", Conditions: [new(Type: "lessThan", Filter: Json("1")), new(Type: "greaterThan", Filter: Json("9"))]),
        }));
        Assert.Contains("(\"dv01\" < @p4 OR \"dv01\" > @p5)", or.Sql);

        var and = Sql(new GridRequest(FilterModel: new()
        {
            ["deal_name"] = new("text", Operator: "AND", Conditions: [new(Type: "startsWith", Filter: Json("\"A\"")), new(Type: "endsWith", Filter: Json("\"3\""))]),
        }));
        Assert.Contains("(\"deal_name\" ILIKE @p4 AND \"deal_name\" ILIKE @p5)", and.Sql);

        Assert.Empty(Normalize(new GridRequest(FilterModel: new()
        {
            ["dv01"] = new("number", Operator: "OR", Conditions: [new(Type: "lessThan", Filter: Json("1")), new(Type: "nope")]),
        })).Filters);

        // More than two conditions apply in full (AG Grid's maxNumConditions); beyond the cap is a 400, not a cut.
        var three = Sql(new GridRequest(FilterModel: new()
        {
            ["sector"] = new("text", Operator: "OR", Conditions: [new(Type: "equals", Filter: Json("\"A\"")), new(Type: "equals", Filter: Json("\"B\"")), new(Type: "equals", Filter: Json("\"C\""))]),
        }));
        Assert.Contains("(\"sector\" ILIKE @p4 OR \"sector\" ILIKE @p5 OR \"sector\" ILIKE @p6)", three.Sql);
        Assert.Throws<GridRequestException>(() => Normalize(new GridRequest(FilterModel: new()
        {
            ["dv01"] = new("number", Operator: "OR", Conditions: Enumerable.Repeat(new FilterSpec(Type: "blank"), GridQueryNormalizer.MaxConditions + 1).ToArray()),
        })));
    }

    [Fact]
    public void Quick_filter_ignores_blank_and_overlong_tokens_and_caps_the_count()
    {
        Assert.Empty(Normalize(new GridRequest(QuickFilter: "   ")).QuickTokens);
        Assert.Empty(Normalize(new GridRequest(QuickFilter: new string('x', 65))).QuickTokens);
        Assert.Equal(GridQueryNormalizer.MaxQuickTokens, Normalize(new GridRequest(QuickFilter: "a b c d e f g")).QuickTokens.Count);
    }

    [Fact]
    public void Filters_are_capped()
    {
        var model = ColumnCatalog.PositionSnapshot.Where(c => GridQueryNormalizer.IsNumeric(c.Kind))
            .ToDictionary(c => c.Name, _ => new FilterSpec("number", "notBlank"));
        Assert.Equal(GridQueryNormalizer.MaxFilters, Normalize(new GridRequest(FilterModel: model)).Filters.Count);
    }

    [Fact]
    public void Equivalent_requests_share_a_canonical_key_and_different_ones_dont()
    {
        var a = Normalize(new GridRequest(Columns: ["dv01", "junk"], FilterModel: new()
        {
            ["sector"] = new("set", Values: ["RMBS", "CLO"]), ["junk"] = new("text", "contains", Json("\"x\"")),
        }, QuickFilter: "CLO clo"));
        var b = Normalize(new GridRequest(Columns: ["dv01"], FilterModel: new() { ["sector"] = new("set", Values: ["CLO", "RMBS"]) }, QuickFilter: "clo"));
        Assert.Equal(a.CanonicalKey, b.CanonicalKey);

        var c = Normalize(new GridRequest(Columns: ["dv01"], FilterModel: new() { ["sector"] = new("set", Values: ["CLO"]) }, QuickFilter: "clo"));
        Assert.NotEqual(a.CanonicalKey, c.CanonicalKey);

        var dated = Normalize(new GridRequest(FilterModel: new()
        {
            ["deal_name"] = new("text", Operator: "OR", Conditions: [new(Type: "startsWith", Filter: Json("\"A\"")), new(Type: "blank")]),
            ["next_pay_date"] = new("date", "inRange", DateFrom: "2026-11-01", DateTo: "2026-12-01"),
            ["spread_bp"] = new("number", "equals", Json("1.5")),
            ["class"] = new("set", Values: ["A", null]),
        }));
        Assert.Contains("2026-11-01", dated.CanonicalKey);
        Assert.Contains("1.5", dated.CanonicalKey);
        Assert.Contains("\"Or\":true", dated.CanonicalKey);
        Assert.NotEqual(dated.CanonicalKey, dated.SummaryKey);
    }

    [Fact]
    public void Delimiters_inside_filter_values_cannot_make_two_queries_share_a_key()
    {
        var crafted = Normalize(new GridRequest(FilterModel: new()
        {
            ["class"] = new("text", "contains", Json(JsonSerializer.Serialize("x,,);deal_name:Text.Contains(y"))),
        }));
        var real = Normalize(new GridRequest(FilterModel: new()
        {
            ["class"] = new("text", "contains", Json("\"x\"")),
            ["deal_name"] = new("text", "contains", Json("\"y\"")),
        }));
        Assert.NotEqual(crafted.CanonicalKey, real.CanonicalKey);
        Assert.NotEqual(crafted.SummaryKey, real.SummaryKey);
    }

    [Fact]
    public void Weighted_average_returns_null_for_zero_or_missing_weights()
    {
        var spread = ColumnCatalog.PositionSnapshot.Single(c => c.Name == "spread_bp");
        Assert.Contains("NULLIF(", GridSqlBuilder.Aggregate(spread));
        Assert.Equal("SUM(\"market_value\")", GridSqlBuilder.Aggregate(ColumnCatalog.PositionSnapshot.Single(c => c.Name == "market_value")));
    }

    [Fact]
    public void Export_sql_has_no_offset_and_no_summary()
    {
        var q = Normalize(new GridRequest(Columns: ["deal_name"])) with { Limit = 25_000 };
        var sql = GridSqlBuilder.BuildExport(q, ColumnCatalog.PositionSnapshot);
        Assert.EndsWith("ORDER BY \"position_id\" ASC LIMIT @limit;", sql.Sql);
        Assert.DoesNotContain("COUNT", sql.Sql);
        Assert.Equal(25_000, sql.Parameters.Single(p => p.Key == "limit").Value);
    }

    [Fact]
    public void Like_escape_and_quote()
    {
        Assert.Equal(@"50\% off\_\\", GridSqlBuilder.EscapeLike(@"50% off_\"));
        Assert.Equal("\"class\"", GridSqlBuilder.Quote("class"));
    }

    [Fact]
    public void Built_in_presets_only_name_catalog_columns_and_risk_is_about_40()
    {
        foreach (var (name, columns) in BuiltInPresets.ByName)
            Assert.All(columns, c => Assert.True(ColumnCatalog.Names.Contains(c), $"{name}: {c}"));
        Assert.InRange(BuiltInPresets.Risk.Count, 35, 45);
        Assert.Equal(100 + 11, BuiltInPresets.Scenarios.Count);
        Assert.DoesNotContain("position_id", BuiltInPresets.All);
    }
}
