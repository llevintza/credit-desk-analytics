namespace Desk.Data.App;

/// <summary>A user's saved grid column state for one page (README §6 P1 column presets). Never leaves the data layer.</summary>
public sealed class Preset
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public required string Page { get; set; }
    public required string Name { get; set; }
    /// <summary>AG Grid column state (order, width, visibility, pinned, sort, filters) as JSON.</summary>
    public required string State { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
