using Desk.Data.App;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Audit;

/// <summary>
/// Drains <see cref="AuditQueue"/> into <c>app.audit</c> in batches, on its own context from the pooled factory.
/// A failed insert is logged and the batch dropped: audit data is best-effort and must never back up into requests.
/// The factory is resolved on the first write, not at startup: the app must boot without a database.
/// </summary>
public sealed class AuditWriter(AuditQueue queue, IServiceProvider services, ILogger<AuditWriter> logger) : BackgroundService
{
    public const int MaxBatch = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AuditEntry>(MaxBatch);
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                Drain(batch);
                await WriteAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: flush what is already queued, briefly, then stop.
            Drain(batch);
            using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WriteAsync(batch, flush.Token);
        }
    }

    private void Drain(List<AuditEntry> batch)
    {
        while (batch.Count < MaxBatch && queue.Reader.TryRead(out var entry))
            batch.Add(entry);
    }

    internal async Task WriteAsync(List<AuditEntry> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        try
        {
            var contexts = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await contexts.CreateDbContextAsync(ct);
            db.Audit.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Dropped {Count} audit entries: the write failed.", batch.Count);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Dropped {Count} audit entries: shutting down.", batch.Count);
        }
        finally
        {
            batch.Clear();
        }
    }
}
