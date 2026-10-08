using System.Data;
using System.Text.Json;
using Desk.Api.Positions;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Desk.Api.Tests;

/// <summary>Serializer, reader and clock edge cases (nulls, NaN, every column kind) without a database.</summary>
public sealed class PositionsUnitTests
{
    private static ColumnDef Def(string name, ColumnKind kind, Aggregation agg = Aggregation.None) => new(name, "g", kind, agg, name);

    /// <summary>One column per kind, each with a value, a null (and a NaN for doubles).</summary>
    private static GridBlock Block()
    {
        var id = new Int64Column(Def("position_id", ColumnKind.Key)); id.Values.AddRange([1, null]);
        var count = new Int32Column(Def("loan_count", ColumnKind.Count)); count.Values.AddRange([7, null]);
        var money = new DecimalColumn(Def("market_value", ColumnKind.Money, Aggregation.Sum)); money.Values.AddRange([1250000.25m, null]);
        var price = new DoubleColumn(Def("price", ColumnKind.Price)); price.Values.AddRange([99.5, double.NaN]);
        var flag = new BoolColumn(Def("watchlist_flag", ColumnKind.Flag)); flag.Values.AddRange([true, null]);
        var date = new DateColumn(Def("next_pay_date", ColumnKind.Date)); date.Values.AddRange([new DateOnly(2026, 11, 25), null]);
        var text = new TextColumn(Def("deal_name", ColumnKind.Text)); text.Values.AddRange(["CLO 2024-3", null]);
        return new GridBlock([id, count, money, price, flag, date, text], 2,
        [
            new(money.Def, 1250000.25m), new(Def("spread_bp", ColumnKind.Bp), 212.4), new(Def("loan_total", ColumnKind.Count), 15L),
            new(Def("yield", ColumnKind.Pct), double.PositiveInfinity), new(Def("dv01", ColumnKind.Money), null),
        ], 0);
    }

    [Fact]
    public void Json_writes_every_kind_nulls_and_turns_nan_into_null()
    {
        var doc = JsonDocument.Parse(ColumnarSerializer.ToJson(Block(), new DateOnly(2026, 10, 6), DateTimeOffset.UnixEpoch)).RootElement;
        var data = doc.GetProperty("data");
        Assert.Equal("[1,null]", data[0].GetRawText());
        Assert.Equal("[7,null]", data[1].GetRawText());
        Assert.Equal("[1250000.25,null]", data[2].GetRawText());
        Assert.Equal("[99.5,null]", data[3].GetRawText()); // NaN → null
        Assert.Equal("[true,null]", data[4].GetRawText());
        Assert.Equal("[\"2026-11-25\",null]", data[5].GetRawText());
        Assert.Equal("[\"CLO 2024-3\",null]", data[6].GetRawText());
        var summary = doc.GetProperty("summary");
        Assert.Equal(1250000.25m, summary.GetProperty("market_value").GetDecimal());
        Assert.Equal(15, summary.GetProperty("loan_total").GetInt64());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("yield").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("dv01").ValueKind);
        Assert.Equal("2026-10-06", doc.GetProperty("asOf").GetString());
    }

    [Fact]
    public void MessagePack_writes_every_kind_and_nulls()
    {
        var bytes = ColumnarSerializer.ToMsgPack(Block(), new DateOnly(2026, 10, 6), DateTimeOffset.UnixEpoch);
        var doc = MessagePack.MessagePackSerializer.Deserialize<Dictionary<string, object?>>(bytes, cancellationToken: TestContext.Current.CancellationToken);
        var data = (object?[])doc["data"]!;
        Assert.All(data, col => Assert.Null(((object?[])col!)[1]));
        Assert.Equal(99.5, ((object?[])data[3]!)[0]);
        Assert.Equal("1250000.25", ((object?[])data[2]!)[0]); // money stays exact: a decimal string, not a float
        Assert.Equal("2026-11-25", ((object?[])data[5]!)[0]);
        var summary = (Dictionary<object, object?>)doc["summary"]!;
        Assert.Equal("1250000.25", summary["market_value"]);
        Assert.Equal(15, Convert.ToInt32(summary["loan_total"]));
        Assert.Null(summary["yield"]);
        Assert.Null(summary["dv01"]);
    }

    [Fact]
    public void Columns_read_values_and_nulls_from_a_data_reader()
    {
        var table = new DataTable();
        table.Columns.Add("position_id", typeof(long));
        table.Columns.Add("loan_count", typeof(int));
        table.Columns.Add("price", typeof(double));
        table.Columns.Add("market_value", typeof(decimal));
        table.Columns.Add("deal_name", typeof(string));
        table.Columns.Add("next_pay_date", typeof(DateOnly));
        table.Columns.Add("watchlist_flag", typeof(bool));
        table.Rows.Add(1L, 7, 99.5, 10.25m, "CLO", new DateOnly(2026, 11, 25), true);
        table.Rows.Add(DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);

        GridColumn[] columns =
        [
            GridColumn.For(Def("position_id", ColumnKind.Key)), GridColumn.For(Def("loan_count", ColumnKind.Count)),
            GridColumn.For(Def("price", ColumnKind.Price)), GridColumn.For(Def("market_value", ColumnKind.Money)),
            GridColumn.For(Def("deal_name", ColumnKind.Text)), GridColumn.For(Def("next_pay_date", ColumnKind.Date)),
            GridColumn.For(Def("watchlist_flag", ColumnKind.Flag)),
        ];
        using var reader = table.CreateDataReader();
        while (reader.Read())
            for (var i = 0; i < columns.Length; i++) columns[i].Read(reader, i);

        Assert.All(columns, c => Assert.Equal(2, c.Count));
        Assert.Equal([1L, null], ((Int64Column)columns[0]).Values);
        Assert.Equal([7, null], ((Int32Column)columns[1]).Values);
        Assert.Equal([99.5, null], ((DoubleColumn)columns[2]).Values);
        Assert.Equal([10.25m, null], ((DecimalColumn)columns[3]).Values);
        Assert.Equal(["CLO", null], ((TextColumn)columns[4]).Values);
        Assert.Equal([new DateOnly(2026, 11, 25), null], ((DateColumn)columns[5]).Values);
        Assert.Equal([true, null], ((BoolColumn)columns[6]).Values);
        Assert.IsType<Int32Column>(GridColumn.For(Def("portfolio_id", ColumnKind.Key)));
        Assert.Equal(0, new GridBlock([], 0, [], 0).Rows);
    }

    [Theory]
    [InlineData("2026-10-07T11:00:00Z", "2026-10-08T10:30:00Z")] // 07:00 EDT → tomorrow 06:30 EDT
    [InlineData("2026-10-07T09:00:00Z", "2026-10-07T10:30:00Z")] // 05:00 EDT → today 06:30 EDT
    [InlineData("2026-12-01T12:00:00Z", "2026-12-02T11:30:00Z")] // winter: 06:30 EST is 11:30 UTC
    [InlineData("2026-10-07T10:30:00Z", "2026-10-08T10:30:00Z")] // exactly at the batch: the next one
    public void Next_batch_is_0630_new_york_after_now(string now, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), BatchClock.NextBatchAfter(DateTimeOffset.Parse(now)));

    [Fact]
    public void Without_tzdata_the_clock_falls_back_to_a_fixed_eastern_offset()
    {
        Assert.Equal(TimeSpan.FromHours(-5), BatchClock.Find("Nowhere/Missing").BaseUtcOffset);
        Assert.Equal("America/New_York", BatchClock.Find("America/New_York").Id);
    }

    [Theory]
    [InlineData(null, 64)]
    [InlineData("0", 64)]
    [InlineData("lots", 64)]
    [InlineData("8", 8)]
    public void Positions_cache_size_defaults_to_64_mb(string? value, int expectedMb)
    {
        using var cache = new PositionsCache(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PositionsCache.SizeConfigKey] = value }).Build(), TimeProvider.System);
        Assert.Equal(expectedMb * 1024L * 1024L, cache.SizeLimitBytes);
        cache.Cache.Set("k", new byte[] { 1 }, new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions { Size = 1 });
        cache.Clear();
        Assert.False(cache.Cache.TryGetValue("k", out _));
    }

    /// <summary>
    /// Entries expire at the batch end, an instant on the app's clock, so the cache must use that clock too. Run with
    /// the fake clock both far behind and far ahead of the real one, so it fails on a wall-clock cache whatever the
    /// date the suite runs on.
    /// </summary>
    [Theory]
    [InlineData(2001)]
    [InlineData(2201)]
    public void Positions_cache_expires_on_the_injected_clock(int year)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(year, 10, 7, 12, 0, 0, TimeSpan.Zero));
        using var cache = new PositionsCache(new ConfigurationBuilder().Build(), time);
        var batchEnd = time.GetUtcNow().AddHours(1);
        cache.Cache.Set("k", new byte[] { 1 }, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpiration = batchEnd });

        Assert.True(cache.Cache.TryGetValue("k", out _));
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.True(cache.Cache.TryGetValue("k", out _));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(cache.Cache.TryGetValue("k", out _));
    }

    [Fact]
    public void A_snapshot_has_data_only_with_a_catalog_and_an_as_of_date()
    {
        var normalizer = new GridQueryNormalizer(ColumnCatalog.PositionSnapshot);
        DateOnly[] dates = [new(2026, 10, 6)];
        var never = DateTimeOffset.MaxValue;
        Assert.True(new MetaSnapshot(ColumnCatalog.PositionSnapshot, normalizer, dates, "v", [], never, never).HasData);
        Assert.False(new MetaSnapshot(ColumnCatalog.PositionSnapshot, normalizer, [], "v", [], never, never).HasData);
        Assert.False(new MetaSnapshot([], null, dates, "v", [], never, never).HasData);
    }

    [Fact]
    public void Accept_negotiation_finds_messagepack_anywhere_in_the_header()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        Assert.False(PositionsEndpoints.WantsMsgPack(http.Request));
        http.Request.Headers.Accept = "application/json, application/x-msgpack;q=0.9";
        Assert.True(PositionsEndpoints.WantsMsgPack(http.Request));
    }

    [Fact]
    public void Unavailable_tells_an_unseeded_database_from_an_invalid_catalog()
    {
        static string? Title(MetaSnapshot meta) =>
            Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(PositionsEndpoints.Unavailable(meta, new GridRequest())).ProblemDetails.Title;
        var catalog = ColumnCatalog.PositionSnapshot;
        var now = DateTimeOffset.UnixEpoch;
        // A usable catalog with no as-of dates yet, and an empty database: both are "not seeded".
        Assert.Equal("No data loaded", Title(new MetaSnapshot(catalog, new GridQueryNormalizer(catalog), [], "v", [], now, now)));
        Assert.Equal("No data loaded", Title(new MetaSnapshot([], null, [], "empty", [], now, now)));
        // Catalog rows but no normalizer: the catalog was refused.
        Assert.Equal("Column catalog invalid", Title(new MetaSnapshot(catalog, null, [], "v", [], now, now)));
    }
}

