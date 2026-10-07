using System.Diagnostics;
using System.Text;

namespace Desk.Api.Tests;

public sealed class StartScriptTests
{
    static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "start.sh");

    [Fact]
    public async Task Missing_database_url_exits_1_with_a_clear_message()
    {
        var result = await RunAsync(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = null,
            ["ConnectionStrings__App"] = null,
        });
        Assert.Equal(1, result.Code);
        Assert.Contains("DATABASE_URL is not set", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionStrings_App_is_enough_to_pass_the_env_check()
    {
        var result = await RunWithFakeDotnetAsync(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = null,
            ["ConnectionStrings__App"] = "Host=localhost;Database=creditdesk;Username=desk",
        });
        Assert.Equal(0, result.Code);
    }

    [Fact]
    public async Task Dev_APP_VERSION_falls_back_to_RENDER_GIT_COMMIT_and_PORT()
    {
        var result = await RunWithFakeDotnetAsync(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=localhost;Database=creditdesk;Username=desk",
            ["APP_VERSION"] = "dev",
            ["RENDER_GIT_COMMIT"] = "abc123def456",
            ["PORT"] = "1234",
        });
        Assert.Equal(0, result.Code);
        Assert.Contains("APP_VERSION=abc123def456", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("PORTS=1234", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_APP_VERSION_is_kept()
    {
        var result = await RunWithFakeDotnetAsync(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=localhost;Database=creditdesk;Username=desk",
            ["APP_VERSION"] = "cafebabe",
            ["RENDER_GIT_COMMIT"] = "should-not-win",
        });
        Assert.Equal(0, result.Code);
        Assert.Contains("APP_VERSION=cafebabe", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("should-not-win", result.Stdout, StringComparison.Ordinal);
    }

    static async Task<(int Code, string Stdout, string Stderr)> RunWithFakeDotnetAsync(
        Dictionary<string, string?> extraEnv)
    {
        var fake = Path.Combine(Path.GetTempPath(), "desk-fake-dotnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fake);
        try
        {
            var dotnet = Path.Combine(fake, "dotnet");
            await File.WriteAllTextAsync(dotnet, """
                #!/bin/sh
                echo "APP_VERSION=$APP_VERSION"
                echo "PORTS=$ASPNETCORE_HTTP_PORTS"
                """);
            var chmod = Process.Start(new ProcessStartInfo("chmod", $"+x {dotnet}") { RedirectStandardOutput = true, RedirectStandardError = true });
            Assert.NotNull(chmod);
            await chmod.WaitForExitAsync(TestContext.Current.CancellationToken);

            extraEnv["PATH"] = fake + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            return await RunAsync(extraEnv);
        }
        finally
        {
            Directory.Delete(fake, recursive: true);
        }
    }

    static async Task<(int Code, string Stdout, string Stderr)> RunAsync(Dictionary<string, string?> extraEnv)
    {
        Assert.True(File.Exists(ScriptPath), $"start.sh was not copied to {ScriptPath}");
        var psi = new ProcessStartInfo("sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add(ScriptPath);
        foreach (var (key, value) in extraEnv)
        {
            psi.Environment.Remove(key);
            if (value is not null) psi.Environment[key] = value;
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start sh");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        await proc.WaitForExitAsync(cts.Token);
        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
