namespace Desk.Seeder.Tests;

public sealed class SeedProgramTests
{
    [Fact]
    public async Task Assembly_entry_point_rejects_bad_arguments()
    {
        using var env = Env.Set(("DATABASE_URL", null), ("ConnectionStrings__App", null));
        var entry = typeof(SeedRunner).Assembly.EntryPoint;
        Assert.NotNull(entry);
        var invoked = entry.Invoke(null, [new[] { "--nope" }]);
        var code = invoked switch
        {
            Task<int> task => await task,
            Task task => await AwaitAndZero(task),
            int i => i,
            _ => throw new InvalidOperationException($"Unexpected entry point return {invoked?.GetType().FullName}"),
        };
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task Assembly_entry_point_rejects_bad_as_of()
    {
        using var env = Env.Set(("DATABASE_URL", null), ("ConnectionStrings__App", null));
        var entry = typeof(SeedRunner).Assembly.EntryPoint;
        Assert.NotNull(entry);
        var invoked = entry.Invoke(null, [new[] { "--if-changed", "--as-of", "not-a-date" }]);
        var code = invoked switch
        {
            Task<int> task => await task,
            int i => i,
            _ => throw new InvalidOperationException($"Unexpected entry point return {invoked?.GetType().FullName}"),
        };
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task Assembly_entry_point_rejects_missing_connection_string()
    {
        using var env = Env.Set(("DATABASE_URL", null), ("ConnectionStrings__App", null));
        var entry = typeof(SeedRunner).Assembly.EntryPoint;
        Assert.NotNull(entry);
        var invoked = entry.Invoke(null, [new[] { "--size-report" }]);
        var code = invoked switch
        {
            Task<int> task => await task,
            int i => i,
            _ => throw new InvalidOperationException($"Unexpected entry point return {invoked?.GetType().FullName}"),
        };
        Assert.Equal(1, code);
    }

    static async Task<int> AwaitAndZero(Task task)
    {
        await task;
        return 0;
    }

    sealed class Env : IDisposable
    {
        readonly List<(string Key, string? Previous)> _previous = [];

        public static Env Set(params (string Key, string? Value)[] pairs)
        {
            var scope = new Env();
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
}
