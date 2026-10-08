using Desk.Data.App;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Audit;

/// <summary>
/// Drains <see cref="AuditQueue"/> into <c>app.audit</c> in batches, on its own context from the pooled factory.
/// A failed insert is logged and the batch dropped: audit data is best-effort and must never back up into requests.
/// The factory is resolved on the first write, not at startup: the app must boot without a database.
/// Writes are coalesced: after the first entry arrives the writer waits <c>AUDIT_FLUSH_SECONDS</c> (default 30) or
/// until a full batch, so an active session costs one insert per interval, not one per request.
/// After an insert, the writer runs the <see cref="AuditRetention"/> purge when it is due (first write after start, then once every 24 h).
/// </summary>
public sealed class AuditWriter(AuditQueue queue, IServiceProvider services, IConfiguration config, AuditRetention retention, ILogger<AuditWriter> logger) : BackgroundService
{
    public const int MaxBatch = 500;
    public const string FlushConfigKey = "AUDIT_FLUSH_SECONDS";

    internal TimeSpan FlushInterval { get; } =
        TimeSpan.FromSeconds(int.TryParse(config[FlushConfigKey], out var s) && s >= 0 ? s : 30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AuditEntry>(MaxBatch);
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                if (queue.Reader.Count < MaxBatch)
                    await Task.Delay(FlushInterval, stoppingToken);
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
            await PurgeAsync(db, ct);
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

    internal async Task PurgeAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            if (await retention.PurgeIfDueAsync(db, ct) is { } deleted)
                logger.LogInformation("Purged {Count} audit entries older than {Days} days.", deleted, retention.Window.TotalDays);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; the batch is already saved, and the next start purges.
        }
        catch (Exception ex)
        {
            // The batch is already saved; a failed purge is retried next interval.
            logger.LogWarning(ex, "Audit retention purge failed.");
        }
    }
}
