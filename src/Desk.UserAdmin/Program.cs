using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace Desk.UserAdmin;

// A namespaced entry point (not top-level statements): the API's global Program stays unambiguous in tests.
internal static class Program
{
    // Exit codes: 0 ok, 1 bad arguments / account problem / no connection string, 3 unexpected error, 130 cancelled.
    // Ctrl+C and SIGTERM cancel the token; both hooks are removed before the source is disposed (see Desk.Seeder).
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCtrlC = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCtrlC;
        var onSigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            return await new UserAdminApp(Console.Out, Console.Error).RunAsync(args, config, cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCtrlC;
            onSigterm.Dispose();
        }
    }
}
