namespace Desk.Data.App;

/// <summary>One row per completed seed run. The deploy pipeline compares Version + Scale to decide whether to reseed.</summary>
public sealed class SeedMetadata
{
    public long Id { get; set; }
    public required string Version { get; set; }
    public int Seed { get; set; }
    public decimal Scale { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public long DatabaseSizeBytes { get; set; }
}
