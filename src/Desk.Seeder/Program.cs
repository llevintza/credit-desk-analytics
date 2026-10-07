using Desk.Data;
using Desk.Seeder;
using Microsoft.Extensions.Configuration;

// Exit codes: 0 ok (seeded or skipped), 1 bad arguments / not migrated, 2 over the size budget.
SeedOptions options;
try { options = SeedOptions.Parse(args); }
catch (Exception e) when (e is ArgumentException or FormatException) { Console.Error.WriteLine($"ERROR: {e.Message}"); return 1; }

string connectionString;
try { connectionString = ConnectionStrings.Resolve(new ConfigurationBuilder().AddEnvironmentVariables().Build(), ConnectionStrings.App); }
catch (InvalidOperationException e) { Console.Error.WriteLine($"ERROR: {e.Message}"); return 1; }

return await SeedRunner.RunAsync(options, connectionString, Console.Out, Console.Error);
