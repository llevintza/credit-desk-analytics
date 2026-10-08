using Desk.Data;
using Microsoft.Extensions.Configuration;

namespace Desk.Seeder;

/// <summary>
/// Parse-and-dispatch entry used by tests. The console host (<c>Program.cs</c>) calls
/// <see cref="SeedRunner"/> directly so it can own signal handling and exit code 130.
/// This is not the phase-0 metadata-only path: every seed goes through <see cref="SeedRunner"/>.
/// </summary>
public static class SeedApp
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        SeedOptions options;
        try { options = SeedOptions.Parse(args); }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"ERROR: {e.Message}");
            return 1;
        }

        string connectionString;
        try
        {
            connectionString = ConnectionStrings.Resolve(
                new ConfigurationBuilder().AddEnvironmentVariables().Build(), ConnectionStrings.App);
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"ERROR: {e.Message}");
            return 1;
        }

        return await SeedRunner.RunAsync(options, connectionString, Console.Out, Console.Error, ct);
    }
}
