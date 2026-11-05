using System.Runtime.InteropServices;

namespace Desk.Mcp;

// A namespaced entry point (not top-level statements), as in Desk.UserAdmin: the API's global Program stays unambiguous in tests.
internal static class Program
{
    // Ctrl+C and SIGTERM cancel the token; both hooks are removed before the source is disposed (see Desk.UserAdmin).
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCtrlC = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCtrlC;
        var onSigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
        try
        {
            await using var stdin = Console.OpenStandardInput();
            await using var stdout = Console.OpenStandardOutput();
            return await new DeskMcpApp(Console.Error, new ConsolePrompt(), Environment.GetEnvironmentVariable, TimeProvider.System)
                .RunAsync(args, stdin, stdout, cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCtrlC;
            onSigterm.Dispose();
        }
    }
}
