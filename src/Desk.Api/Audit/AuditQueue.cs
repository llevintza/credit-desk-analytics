using System.Diagnostics;
using System.Threading.Channels;
using Desk.Data.App;

namespace Desk.Api.Audit;

/// <summary>
/// In-memory hand-off between the request path and <see cref="AuditWriter"/>. Bounded and non-blocking: when it
/// is full the entry is dropped and counted, so auditing can never slow down or fail a request (README §7.2).
/// </summary>
public sealed class AuditQueue
{
    public const int Capacity = 10_000;

    private readonly Channel<AuditEntry> _channel;
    private long _dropped;

    public AuditQueue(int capacity = Capacity)
    {
        _channel = Channel.CreateBounded<AuditEntry>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
    }

    internal ChannelReader<AuditEntry> Reader => _channel.Reader;

    /// <summary>Entries dropped because the queue was full, since process start.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Enqueue(AuditEntry entry) => _channel.Writer.TryWrite(entry);

    public void Enqueue(string kind, string? userName, string endpoint, int status, long startedTimestamp, TimeProvider time, int? rows = null, string? cache = null) =>
        Enqueue(new AuditEntry
        {
            At = time.GetUtcNow(),
            Kind = kind,
            UserName = Truncate(userName, 256),
            Endpoint = Truncate(endpoint, 256)!,
            Status = status,
            Rows = rows,
            Ms = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds),
            Cache = cache,
        });

    internal void Complete() => _channel.Writer.TryComplete();

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
