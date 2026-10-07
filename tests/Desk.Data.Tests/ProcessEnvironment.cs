namespace Desk.Data.Tests;

/// <summary>
/// Serializes tests that mutate process environment variables (the design-time factory reads them).
/// </summary>
[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

public sealed class ProcessEnvironment : IDisposable
{
    readonly List<(string Key, string? Previous)> _previous = [];

    public static ProcessEnvironment Set(params (string Key, string? Value)[] pairs)
    {
        var scope = new ProcessEnvironment();
        foreach (var (key, value) in pairs)
        {
            scope._previous.Add((key, Environment.GetEnvironmentVariable(key)));
            Environment.SetEnvironmentVariable(key, value);
        }
        return scope;
    }

    public void Dispose()
    {
        foreach (var (key, previous) in _previous)
            Environment.SetEnvironmentVariable(key, previous);
    }
}
