using Desk.Data;
using Desk.Seeder;
using Microsoft.Extensions.Configuration;

// Exit codes: 0 ok (seeded or skipped), 1 bad arguments / not migrated, 2 over the size budget, 130 cancelled.
SeedOptions options;
try { options = SeedOptions.Parse(args); }
catch (Exception e) when (e is ArgumentException or FormatException) { Console.Error.WriteLine($"ERROR: {e.Message}"); return 1; }

string connectionString;
try { connectionString = ConnectionStrings.Resolve(new ConfigurationBuilder().AddEnvironmentVariables().Build(), ConnectionStrings.App); }
catch (InvalidOperationException e) { Console.Error.WriteLine($"ERROR: {e.Message}"); return 1; }

// Ctrl+C / SIGTERM (CI job timeout) cancels cleanly: the seeding transaction rolls back.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
try { return await SeedRunner.RunAsync(options, connectionString, Console.Out, Console.Error, cts.Token); }
catch (OperationCanceledException) { Console.Error.WriteLine("ERROR: cancelled; the seeding transaction was rolled back."); return 130; }
