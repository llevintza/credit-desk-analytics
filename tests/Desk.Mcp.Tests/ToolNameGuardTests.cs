using System.Diagnostics;
using ModelContextProtocol.Client;

namespace Desk.Mcp.Tests;

/// <summary>
/// ADR-0031 §7: every tool the server offers passes the agent guard. The names come from the running server's
/// <c>tools/list</c>, and each goes through <c>decideMcp</c> in scripts/agents/hooks/lib/mcp.mjs from this checkout,
/// as <c>mcp__desk__&lt;tool&gt;</c> with no arguments (the meta tools take none), run by node.
/// </summary>
[Collection(DeskApiCollection.Name)]
public sealed class ToolNameGuardTests(DeskApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Script = """
        import { pathToFileURL } from "node:url";
        import { resolve } from "node:path";
        const [root, ...names] = process.argv.slice(1);
        const { decideMcp } = await import(pathToFileURL(resolve(root, "scripts/agents/hooks/lib/mcp.mjs")).href);
        for (const name of names) {
          const d = decideMcp({ tool: `mcp__desk__${name}`, args: {} });
          console.log(`${d.code === 0 ? "allow" : "BLOCK"} mcp__desk__${name}`);
        }
        const control = decideMcp({ tool: "mcp__desk__dispatch_ingest", args: {} });
        console.log(`${control.code === 0 ? "allow" : "BLOCK"} mcp__desk__dispatch_ingest`);
        """;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CreditDesk.slnx"))) return dir.FullName;
        throw new InvalidOperationException("CreditDesk.slnx not found above the test output");
    }

    [Fact]
    public async Task Every_offered_tool_name_passes_decideMcp()
    {
        using var home = new ConfigHome();
        await using var session = await McpSession.StartAsync(api.Host(), home);
        var names = (await session.Client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Order().ToList();
        Assert.Equal(3, names.Count);

        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "--input-type=module", "-e", Script, RepoRoot() }.Concat(names)) start.ArgumentList.Add(arg);
        using var node = Process.Start(start)!;
        var stdout = node.StandardOutput.ReadToEndAsync(Ct);
        var stderr = node.StandardError.ReadToEndAsync(Ct);
        await node.WaitForExitAsync(Ct);
        Assert.True(node.ExitCode == 0, await stderr);

        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(names.Select(n => $"allow mcp__desk__{n}").Append("BLOCK mcp__desk__dispatch_ingest"), lines);
        TestContext.Current.SendDiagnosticMessage(string.Join(Environment.NewLine, lines));
    }
}
