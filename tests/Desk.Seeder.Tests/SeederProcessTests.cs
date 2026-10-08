using System.Diagnostics;

namespace Desk.Seeder.Tests;

/// <summary>
/// Runs the real seeder executable (Program.cs, signal hooks and all) as a child process. The deploy, db-ops
/// and CI pipelines gate on its exit code, so a crash after a successful run (e.g. a shutdown hook touching
/// a disposed CancellationTokenSource, exit 134) must fail here.
/// </summary>
public sealed class SeederProcessTests(SeededDatabase db) : IClassFixture<SeededDatabase>
{
    [Theory]
    [InlineData("--size-report", 0)]
    [InlineData("--if-changed --scale 0.1 --as-of 2026-10-06", 0)]
    [InlineData("", 1)]                          // no mode: refused before touching the database
    [InlineData("--size-report --max-mb 1", 2)]  // over budget (size-report is allowed to fail)
    [InlineData("--if-changed --scale 0.1 --as-of 2026-10-06 --max-mb 1", 0)] // skip must not fail the deploy
    [InlineData("--if-changed --scale 0.1 --as-of 2026-10-06 --cap-mb 1", 0)] // nor must the peak cap
    [InlineData("--force --scale 0.1 --as-of 2026-10-06 --cap-mb 1", 2)]     // reseed peak over the cap: refused before TRUNCATE
    [InlineData("--force --cap-mb 0", 1)]                                     // cap must be positive
    [InlineData("--force --cap-mb 99999999999999999999", 1)]                  // overflow is a bad argument, not a crash (R217-05)
    public async Task Exit_code_is_what_the_pipelines_expect(string args, int expected)
    {
        var (code, stdout, stderr) = await RunAsync(args, db.ConnectionString);
        Assert.True(code == expected, $"exit {code}, expected {expected}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.DoesNotContain("Unhandled exception", stderr);
        if (args.Contains("--if-changed", StringComparison.Ordinal) && expected == 0)
            Assert.Contains("SEED_ACTION=skipped", stdout);
    }

    [Fact]
    public async Task Unexpected_connection_failure_exits_3()
    {
        var (code, _, stderr) = await RunAsync("--size-report",
            "Host=127.0.0.1;Port=1;Database=creditdesk;Username=desk;Timeout=1");
        Assert.Equal(3, code);
        Assert.DoesNotContain("Unhandled exception", stderr);
        Assert.Contains("ERROR:", stderr);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string args, string connectionString)
    {
        var seeder = typeof(SeedRunner).Assembly.Location; // Desk.Seeder.dll copied next to the tests
        var psi = new ProcessStartInfo("dotnet", $"\"{seeder}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ConnectionStrings__App"] = connectionString;
        psi.Environment.Remove("DATABASE_URL");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (p.ExitCode, await stdout, await stderr);
    }
}
