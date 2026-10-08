using System.Data.Common;
using Desk.Data.Catalog;

namespace Desk.Data.Grid;

/// <summary>
/// One column of a grid block, read straight from the data reader into a typed list (no per-cell boxing). The
/// API serializes these as <c>data[col][row]</c> (README §8 columnar DTO).
/// </summary>
public abstract class GridColumn(ColumnDef def)
{
    public ColumnDef Def { get; } = def;
    public abstract int Count { get; }
    internal abstract void Read(DbDataReader reader, int ordinal);

    public static GridColumn For(ColumnDef def) => def.Kind switch
    {
        ColumnKind.Key when def.Name == GridQueryNormalizer.RowIdColumn => new Int64Column(def),
        ColumnKind.Key or ColumnKind.Count => new Int32Column(def),
        ColumnKind.Text => new TextColumn(def),
        ColumnKind.Date => new DateColumn(def),
        ColumnKind.Money => new DecimalColumn(def),
        ColumnKind.Flag => new BoolColumn(def),
        _ => new DoubleColumn(def),
    };
}

public abstract class GridColumn<T>(ColumnDef def) : GridColumn(def)
{
    public List<T> Values { get; } = new(capacity: 256);
    public override int Count => Values.Count;
}

public sealed class Int64Column(ColumnDef def) : GridColumn<long?>(def)
{
    internal override void Read(DbDataReader r, int i) => Values.Add(r.IsDBNull(i) ? null : r.GetInt64(i));
}

public sealed class Int32Column(ColumnDef def) : GridColumn<int?>(def)
{
    internal override void Read(DbDataReader r, int i) => Values.Add(r.IsDBNull(i) ? null : r.GetInt32(i));
}

public sealed class DoubleColumn(ColumnDef def) : GridColumn<double?>(def)
{
    internal override void Read(DbDataReader r, int i) => Values.Add(r.IsDBNull(i) ? null : r.GetDouble(i));
}

public sealed class DecimalColumn(ColumnDef def) : GridColumn<decimal?>(def)
{
    internal override void Read(DbDataReader r, int i) => Values.Add(r.IsDBNull(i) ? null : r.GetDecimal(i));
}

public sealed class TextColumn(ColumnDef def) : GridColumn<string?>(def)
{
    internal override void Read(DbDataReader r, int i) => Values.Add(r.IsDBNull(i) ? null : r.GetString(i));
}

public sealed class DateColumn(ColumnDef def) : GridColumn<DateOnly?>(def)
{
    internal override void Read(DbDataReader r, int i) => Values.Add(r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i));
}

public sealed class BoolColumn(ColumnDef def) : GridColumn<bool?>(def)
{
    internal override void Read(DbDataReader r, int i) => Values.Add(r.IsDBNull(i) ? null : r.GetBoolean(i));
}

/// <summary>A summary value: SUM (decimal for money, else double) or a weighted average (double); null when empty.</summary>
public sealed record SummaryValue(ColumnDef Column, object? Value);

/// <summary>The totals for one filtered view: matching row count and the per-column aggregates.</summary>
public sealed record GridSummary(int RowCount, IReadOnlyList<SummaryValue> Values);

/// <summary>One block of the P1 grid: the rows asked for, the total matching rows and the totals over all of them.</summary>
public sealed record GridBlock(IReadOnlyList<GridColumn> Columns, int RowCount, IReadOnlyList<SummaryValue> Summary, double DbMs)
{
    public int Rows => Columns.Count == 0 ? 0 : Columns[0].Count;
}
