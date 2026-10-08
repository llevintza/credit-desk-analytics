using System.Runtime.InteropServices;
using Desk.Data;
using Desk.Seeder;
using Microsoft.Extensions.Configuration;

// Exit codes: 0 ok (seeded or skipped), 1 bad arguments / not migrated, 2 over the size budget,
// 3 unexpected error, 130 cancelled.
SeedOptions options;
try { options = SeedOptions.Parse(args); }
catch (Exception e) when (e is ArgumentException or FormatException) { Console.Error.WriteLine($"ERROR: {e.Message}"); return 1; }

string connectionString;
try { connectionString = ConnectionStrings.Resolve(new ConfigurationBuilder().AddEnvironmentVariables().Build(), ConnectionStrings.App); }
catch (InvalidOperationException e) { Console.Error.WriteLine($"ERROR: {e.Message}"); return 1; }

// Ctrl+C (SIGINT) and SIGTERM (CI job timeout, container stop) cancel cleanly: the seeding transaction rolls back.
// Both hooks are removed before the CancellationTokenSource is disposed. A process-exit hook would run AFTER
// Main returns and hit the disposed source (ObjectDisposedException, exit 134 after a successful seed).
var cts = new CancellationTokenSource();
ConsoleCancelEventHandler onCtrlC = (_, e) => { e.Cancel = true; cts.Cancel(); };
Console.CancelKeyPress += onCtrlC;
var onSigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
try
{
    return await SeedRunner.RunAsync(options, connectionString, Console.Out, Console.Error, cts.Token);
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("ERROR: cancelled; the seeding transaction was rolled back.");
    return 130;
}
catch (Exception e)
{
    Console.Error.WriteLine($"ERROR: {e.GetType().Name}: {e.Message}");
    return 3;
}
finally
{
    Console.CancelKeyPress -= onCtrlC;
    onSigterm.Dispose();
    cts.Dispose();
}
