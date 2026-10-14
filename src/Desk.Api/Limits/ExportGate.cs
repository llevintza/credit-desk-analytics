using System.Collections.Concurrent;

namespace Desk.Api.Limits;

/// <summary>
/// Who may start an export (README §7.2, #127). An export holds a database permit and a pooled connection for the
/// whole stream, so there is one per user and at most <see cref="LimitsOptions.ExportSlots"/> across all users,
/// below the shared database permits: exports can never take every connection from interactive reads.
/// Both are taken without waiting; the caller answers 429 and releases with <see cref="End"/> in a finally.
/// </summary>
public sealed class ExportGate(LimitsOptions limits)
{
    public enum Result { Started, UserBusy, Full }

    private readonly ConcurrentDictionary<string, byte> _users = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(limits.ExportSlots, limits.ExportSlots);

    public Result TryBegin(string user)
    {
        if (!_users.TryAdd(user, 0))
            return Result.UserBusy;
        if (_slots.Wait(0))
            return Result.Started;
        _users.TryRemove(user, out _);
        return Result.Full;
    }

    public void End(string user)
    {
        if (_users.TryRemove(user, out _))
            _slots.Release();
    }

    /// <summary>Exports running now (diagnostics and tests).</summary>
    public int Running => limits.ExportSlots - _slots.CurrentCount;

    public bool IsRunning(string user) => _users.ContainsKey(user);
}

/// <summary>
/// Marks an endpoint as an export: <see cref="ExportGate"/> caps it, so the per-user concurrency limiter skips it.
/// Matched on the routed endpoint, not the path string, so every spelling routing accepts is treated alike.
/// </summary>
public sealed class ExportEndpoint
{
    public static readonly ExportEndpoint Instance = new();

    private ExportEndpoint() { }
}
