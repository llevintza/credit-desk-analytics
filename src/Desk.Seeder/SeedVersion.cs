namespace Desk.Seeder;

public static class SeedVersion
{
    /// <summary>
    /// Bump whenever the generator or the seeded schema changes. The deploy pipeline reseeds
    /// production only when this (or the scale) differs from the last row in app.seed_metadata.
    /// </summary>
    public const string Current = "1.0.0";
}
