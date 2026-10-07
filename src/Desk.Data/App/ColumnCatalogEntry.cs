namespace Desk.Data.App;

/// <summary>Row of app.column_catalog: the wide snapshot's columns as data (README §5.3), owned by the seeder.</summary>
public sealed class ColumnCatalogEntry
{
    public required string Name { get; set; }
    public int Ordinal { get; set; }
    public required string Group { get; set; }
    public required string Kind { get; set; }
    public required string Aggregation { get; set; }
    public required string Header { get; set; }
}
