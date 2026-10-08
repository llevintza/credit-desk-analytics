using System.Buffers;
using System.Text.Json;
using Desk.Data.Grid;
using MessagePack;

namespace Desk.Api.Positions;

/// <summary>
/// Writes a <see cref="GridBlock"/> as the columnar DTO (README §6 P1, ADR-0007):
/// <c>{ columns, data[col][row], rowCount, summary, asOf, generatedAt }</c>. The hot path writes straight from the
/// typed column buffers with no intermediate DTO, so each value is written once and never boxed.
/// </summary>
public static class ColumnarSerializer
{
    public const string JsonContentType = "application/json";
    public const string MsgPackContentType = "application/x-msgpack";

    public static byte[] ToJson(GridBlock block, DateOnly asOf, DateTimeOffset generatedAt)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("columns");
            foreach (var c in block.Columns) w.WriteStringValue(c.Def.Name);
            w.WriteEndArray();

            w.WriteStartArray("data");
            foreach (var c in block.Columns)
            {
                w.WriteStartArray();
                WriteJsonColumn(w, c);
                w.WriteEndArray();
            }
            w.WriteEndArray();

            w.WriteNumber("rowCount", block.RowCount);
            w.WriteStartObject("summary");
            foreach (var s in block.Summary)
            {
                w.WritePropertyName(s.Column.Name);
                WriteJsonScalar(w, s.Value);
            }
            w.WriteEndObject();
            w.WriteString("asOf", asOf.ToString("yyyy-MM-dd"));
            w.WriteString("generatedAt", generatedAt);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteJsonColumn(Utf8JsonWriter w, GridColumn column)
    {
        switch (column)
        {
            case Int64Column c: foreach (var v in c.Values) if (v is { } x) w.WriteNumberValue(x); else w.WriteNullValue(); break;
            case Int32Column c: foreach (var v in c.Values) if (v is { } x) w.WriteNumberValue(x); else w.WriteNullValue(); break;
            case DecimalColumn c: foreach (var v in c.Values) if (v is { } x) w.WriteNumberValue(x); else w.WriteNullValue(); break;
            case DoubleColumn c: foreach (var v in c.Values) WriteJsonDouble(w, v); break;
            case BoolColumn c: foreach (var v in c.Values) if (v is { } x) w.WriteBooleanValue(x); else w.WriteNullValue(); break;
            case DateColumn c: foreach (var v in c.Values) if (v is { } x) w.WriteStringValue(x.ToString("yyyy-MM-dd")); else w.WriteNullValue(); break;
            case TextColumn c: foreach (var v in c.Values) if (v is not null) w.WriteStringValue(v); else w.WriteNullValue(); break;
        }
    }

    /// <summary>JSON has no NaN or infinity: those become null (README §8: never NaN).</summary>
    private static void WriteJsonDouble(Utf8JsonWriter w, double? v)
    {
        if (v is { } x && double.IsFinite(x)) w.WriteNumberValue(x);
        else w.WriteNullValue();
    }

    private static void WriteJsonScalar(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case decimal d: w.WriteNumberValue(d); break;
            case double d: WriteJsonDouble(w, d); break;
            case long l: w.WriteNumberValue(l); break; // SUM over an integer column is bigint
            default: w.WriteNullValue(); break;
        }
    }

    /// <summary>
    /// The same document as MessagePack (on <c>Accept: application/x-msgpack</c>). MessagePack has no decimal, so
    /// money goes as float64: exact to the cent below about 9e13, far above any position here.
    /// </summary>
    public static byte[] ToMsgPack(GridBlock block, DateOnly asOf, DateTimeOffset generatedAt)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        var w = new MessagePackWriter(buffer);
        w.WriteMapHeader(6);
        w.Write("columns");
        w.WriteArrayHeader(block.Columns.Count);
        foreach (var c in block.Columns) w.Write(c.Def.Name);

        w.Write("data");
        w.WriteArrayHeader(block.Columns.Count);
        foreach (var c in block.Columns)
        {
            w.WriteArrayHeader(c.Count);
            WriteMsgPackColumn(ref w, c);
        }

        w.Write("rowCount");
        w.Write(block.RowCount);
        w.Write("summary");
        w.WriteMapHeader(block.Summary.Count);
        foreach (var s in block.Summary)
        {
            w.Write(s.Column.Name);
            switch (s.Value)
            {
                case decimal d: w.Write((double)d); break;
                case long l: w.Write(l); break;
                case double d when double.IsFinite(d): w.Write(d); break;
                default: w.WriteNil(); break;
            }
        }
        w.Write("asOf");
        w.Write(asOf.ToString("yyyy-MM-dd"));
        w.Write("generatedAt");
        w.Write(generatedAt.ToString("O"));
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMsgPackColumn(ref MessagePackWriter w, GridColumn column)
    {
        switch (column)
        {
            case Int64Column c: foreach (var v in c.Values) if (v is { } x) w.Write(x); else w.WriteNil(); break;
            case Int32Column c: foreach (var v in c.Values) if (v is { } x) w.Write(x); else w.WriteNil(); break;
            case DecimalColumn c: foreach (var v in c.Values) if (v is { } x) w.Write((double)x); else w.WriteNil(); break;
            case DoubleColumn c: foreach (var v in c.Values) if (v is { } x && double.IsFinite(x)) w.Write(x); else w.WriteNil(); break;
            case BoolColumn c: foreach (var v in c.Values) if (v is { } x) w.Write(x); else w.WriteNil(); break;
            case DateColumn c: foreach (var v in c.Values) if (v is { } x) w.Write(x.ToString("yyyy-MM-dd")); else w.WriteNil(); break;
            case TextColumn c: foreach (var v in c.Values) w.Write(v); break;
        }
    }
}
