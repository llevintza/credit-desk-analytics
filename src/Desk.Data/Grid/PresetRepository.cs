using Desk.Data.App;
using Microsoft.EntityFrameworkCore;

namespace Desk.Data.Grid;

public sealed record PresetDto(string Name, string State, DateTimeOffset UpdatedAt);

/// <summary>User column presets on EF Core (README §8: stable model, AsNoTracking projections, set-based writes).</summary>
public sealed class PresetRepository(IDbContextFactory<AppDbContext> contexts, TimeProvider time)
{
    public const int MaxPresetsPerPage = 50;

    public async Task<IReadOnlyList<PresetDto>> ListAsync(Guid userId, string page, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Presets.AsNoTracking()
            .Where(p => p.UserId == userId && p.Page == page)
            .OrderBy(p => p.Name)
            .Select(p => new PresetDto(p.Name, p.State, p.UpdatedAt))
            .ToListAsync(ct);
    }

    /// <summary>Insert or replace by (user, page, name). Returns false when the user is at the per-page limit.</summary>
    public async Task<bool> SaveAsync(Guid userId, string page, string name, string state, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var updated = await db.Presets
            .Where(p => p.UserId == userId && p.Page == page && p.Name == name)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, state).SetProperty(p => p.UpdatedAt, now), ct);
        if (updated > 0) return true;

        if (await db.Presets.CountAsync(p => p.UserId == userId && p.Page == page, ct) >= MaxPresetsPerPage)
            return false;
        db.Presets.Add(new Preset { UserId = userId, Page = page, Name = name, State = state, UpdatedAt = now });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two saves of a new name raced on the unique index: the other one inserted; last write wins.
            db.ChangeTracker.Clear();
            await db.Presets
                .Where(p => p.UserId == userId && p.Page == page && p.Name == name)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, state).SetProperty(p => p.UpdatedAt, now), ct);
        }
        return true;
    }

    public async Task<bool> DeleteAsync(Guid userId, string page, string name, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Presets.Where(p => p.UserId == userId && p.Page == page && p.Name == name).ExecuteDeleteAsync(ct) > 0;
    }
}
