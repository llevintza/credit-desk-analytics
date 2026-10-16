using System.Globalization;
using System.Text.RegularExpressions;
using Desk.Api.Limits;
using Microsoft.Extensions.Configuration;

namespace Desk.Api.Tests;

/// <summary>
/// #143: the raised e2e and benchmark limits never reach a production path. Production runs on the README §7.2
/// defaults; only <c>e2e/docker-compose.e2e.yml</c> (the CI e2e job) and the budgets job's <c>$RUNNER_TEMP</c>
/// override in ci.yml may raise them, and neither is scanned here.
/// </summary>
public sealed partial class ProductionLimitsGuardTests
{
    /// <summary>README §7.2. Changing a default is a deliberate edit here too.</summary>
    private static readonly LimitsOptions Defaults = new(
        PerUserPerMinute: 60, PerUserBurst: 20, LoginPerIpPerMinute: 5, GlobalConcurrency: 8, GlobalQueue: 32,
        PerUserConcurrency: 1, PerUserQueue: 8, InsightsConcurrency: 5, InsightsQueue: 15, ExportSlots: 2,
        ExportTimeout: TimeSpan.FromSeconds(60), ExportMaxRows: 25_000);

    /// <summary>Every key <see cref="LimitsOptions.From"/> reads, with the largest value a production path may carry.</summary>
    private static readonly IReadOnlyDictionary<string, double> Ceilings = new Dictionary<string, double>
    {
        ["RATE_LIMIT_PER_USER_PER_MIN"] = Defaults.PerUserPerMinute,
        ["RATE_LIMIT_PER_USER_BURST"] = Defaults.PerUserBurst,
        ["RATE_LIMIT_LOGIN_PER_IP_PER_MIN"] = Defaults.LoginPerIpPerMinute,
        ["RATE_LIMIT_GLOBAL_CONCURRENCY"] = Defaults.GlobalConcurrency,
        ["RATE_LIMIT_GLOBAL_QUEUE"] = Defaults.GlobalQueue,
        ["RATE_LIMIT_PER_USER_CONCURRENCY"] = Defaults.PerUserConcurrency,
        ["RATE_LIMIT_PER_USER_QUEUE"] = Defaults.PerUserQueue,
        ["RATE_LIMIT_PER_USER_INSIGHTS_CONCURRENCY"] = Defaults.InsightsConcurrency,
        ["RATE_LIMIT_PER_USER_INSIGHTS_QUEUE"] = Defaults.InsightsQueue,
        ["EXPORT_GLOBAL_SLOTS"] = Defaults.ExportSlots,
        ["EXPORT_TIMEOUT_SECONDS"] = Defaults.ExportTimeout.TotalSeconds,
        ["EXPORT_MAX_ROWS"] = Defaults.ExportMaxRows,
    };

    /// <summary>
    /// Fields no setting can raise (#277): <c>EXPORT_MAX_ROWS</c> may only lower the built-in cap, so a raised value is
    /// clamped and leaves the field unchanged. The scan still flags one above the cap; the coverage check lowers it.
    /// </summary>
    private static readonly HashSet<string> LowerOnly = [nameof(LimitsOptions.ExportMaxRows)];

    [Fact]
    public void Defaults_are_the_readme_limits()
    {
        Assert.Equal(Defaults, LimitsOptions.From(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void Ceilings_cover_every_key_LimitsOptions_reads()
    {
        // A new limit key must be added to the scan: setting each listed key to a raised value changes every field.
        var raised = new ConfigurationBuilder().AddInMemoryCollection(
            Ceilings.Select(c => new KeyValuePair<string, string?>(c.Key, (c.Value + 1).ToString(CultureInfo.InvariantCulture)))).Build();
        var options = LimitsOptions.From(raised);
        var unchanged = typeof(LimitsOptions).GetProperties()
            .Where(p => !LowerOnly.Contains(p.Name) && Equals(p.GetValue(options), p.GetValue(Defaults)))
            .Select(p => p.Name);
        Assert.Empty(unchanged);

        // A lower-only field is read from its key all the same: lowering it changes it.
        var lowered = LimitsOptions.From(new ConfigurationBuilder().AddInMemoryCollection(
            Ceilings.Select(c => new KeyValuePair<string, string?>(c.Key, "1"))).Build());
        Assert.All(LowerOnly, name => Assert.NotEqual(
            typeof(LimitsOptions).GetProperty(name)!.GetValue(Defaults), typeof(LimitsOptions).GetProperty(name)!.GetValue(lowered)));
    }

    [Fact]
    public void Production_appsettings_resolve_to_the_defaults()
    {
        var api = Path.Combine(RepoRoot, "src", "Desk.Api");
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(api, "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(api, "appsettings.Production.json"), optional: true)
            .Build();
        Assert.Equal(Defaults, LimitsOptions.From(config));
    }

    [Fact]
    public void Production_paths_never_raise_a_limit()
    {
        var files = ProductionPaths().ToList();
        Assert.Contains(files, f => f.EndsWith("render.yaml", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("deploy.yml", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("docker-compose.yml", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("Dockerfile", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("Program.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.Contains("e2e", StringComparison.Ordinal));

        var raised = files.SelectMany(f => RaisedLimits(File.ReadAllText(f)).Select(r => $"{Path.GetRelativePath(RepoRoot, f)}: {r}")).ToList();
        Assert.Empty(raised);
    }

    [Fact]
    public void Program_cs_sets_no_limit_itself()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot, "src", "Desk.Api", "Program.cs"));
        Assert.DoesNotContain(Ceilings.Keys, k => program.Contains(k, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("RATE_LIMIT_PER_USER_PER_MIN=6000")] // .env style, Dockerfile ENV, start.sh export
    [InlineData("ENV RATE_LIMIT_PER_USER_BURST 1000")] // Dockerfile ENV without =
    [InlineData("      RATE_LIMIT_LOGIN_PER_IP_PER_MIN: \"100\"")] // compose map (the e2e override)
    [InlineData("      - RATE_LIMIT_PER_USER_PER_MIN=6000")] // compose list
    [InlineData("      RATE_LIMIT_PER_USER_PER_MIN: ${RATE_LIMIT_PER_USER_PER_MIN:-6000}")] // compose default
    [InlineData("  \"RATE_LIMIT_PER_USER_PER_MIN\": 6000,")] // appsettings, number
    [InlineData("  \"RATE_LIMIT_PER_USER_PER_MIN\": \"6000\",")] // appsettings, string
    [InlineData("      - key: RATE_LIMIT_PER_USER_PER_MIN\n        value: \"6000\"")] // render.yaml
    [InlineData("          RATE_LIMIT_PER_USER_BURST: '100000'")] // workflow env
    [InlineData("      - key: EXPORT_TIMEOUT_SECONDS\n        value: \"600\"")]
    [InlineData("EXPORT_GLOBAL_SLOTS=3")]
    [InlineData("RATE_LIMIT_PER_USER_PER_MIN=6000 # temporary")] // a comment doesn't hide a raised value
    [InlineData("RATE_LIMIT_PER_USER_INSIGHTS_QUEUE=16")]
    [InlineData("EXPORT_MAX_ROWS=100000")] // clamped to 25,000 anyway, but not a production setting
    public void A_seeded_regression_is_caught(string text)
    {
        Assert.NotEmpty(RaisedLimits(text));
    }

    [Theory]
    [InlineData("      - key: RATE_LIMIT_PER_USER_PER_MIN\n        value: \"60\"\n      - key: RATE_LIMIT_GLOBAL_CONCURRENCY\n        value: \"8\"")]
    [InlineData("RATE_LIMIT_PER_USER_PER_MIN=30")] // tighter is fine
    [InlineData("      - key: RATE_LIMIT_PER_USER_PER_MIN\n        sync: false\n      - key: OTHER\n        value: \"9999\"")] // value set outside git
    [InlineData("# raise RATE_LIMIT_PER_USER_PER_MIN locally, never here")]
    [InlineData("EXPORT_TIMEOUT_SECONDS=0.5")]
    [InlineData("RATE_LIMIT_PER_USER_PER_MIN=60 # was 100")] // a trailing comment isn't a value
    [InlineData("      - key: RATE_LIMIT_PER_USER_PER_MIN\n        value: \"60\" # e2e uses 6000")]
    public void Default_or_tighter_values_pass(string text)
    {
        Assert.Empty(RaisedLimits(text));
    }

    [Fact]
    public void The_e2e_override_is_raised_and_stays_out_of_the_scan()
    {
        // The one allowed place: the guard would flag it, so it must stay outside ProductionPaths.
        var e2e = Path.Combine(RepoRoot, "e2e", "docker-compose.e2e.yml");
        Assert.NotEmpty(RaisedLimits(File.ReadAllText(e2e)));
        Assert.DoesNotContain(e2e, ProductionPaths());
    }

    /// <summary>The paths a production deploy reads (#143): appsettings, Program.cs, render.yaml, deploy.yml, the root
    /// compose files and deploy/.</summary>
    private static IEnumerable<string> ProductionPaths()
    {
        var src = Path.Combine(RepoRoot, "src");
        var appsettings = Directory.EnumerateFiles(src, "appsettings*.json", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(d => d is "bin" or "obj"));
        var rootCompose = Directory.EnumerateFiles(RepoRoot, "*.y*ml")
            .Where(f => Path.GetFileName(f) is var n && (n.StartsWith("docker-compose", StringComparison.Ordinal) || n.StartsWith("compose", StringComparison.Ordinal)));
        var deployDir = Path.Combine(RepoRoot, "deploy");
        Assert.True(Directory.Exists(deployDir), $"{deployDir} is missing: update the production paths this guard scans.");
        var deploy = Directory.EnumerateFiles(deployDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".md", StringComparison.Ordinal));
        return appsettings.Concat(rootCompose).Concat(deploy).Concat(
        [
            Path.Combine(src, "Desk.Api", "Program.cs"),
            Path.Combine(RepoRoot, "render.yaml"),
            Path.Combine(RepoRoot, ".github", "workflows", "deploy.yml"),
        ]).Order(StringComparer.Ordinal);
    }

    /// <summary>Each limit key in <paramref name="text"/> whose value, on its line or on a render.yaml
    /// <c>value:</c> line right after it, is above the default. A key with no value in git passes; YAML, shell and
    /// Dockerfile comments (<c>#</c> at the start or after a space) are ignored.</summary>
    private static IEnumerable<string> RaisedLimits(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(l => Comment().Replace(l, "")).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var (key, ceiling) in Ceilings)
            {
                var at = Key(key).Match(lines[i]);
                if (!at.Success)
                    continue;
                var rest = lines[i][(at.Index + key.Length)..];
                if (i + 1 < lines.Length && RenderValue().Match(lines[i + 1]) is { Success: true } next)
                    rest += " " + next.Groups[1].Value;
                foreach (Match n in Number().Matches(rest))
                    if (double.Parse(n.Value, CultureInfo.InvariantCulture) > ceiling)
                        yield return $"{key}={n.Value} (default {ceiling.ToString(CultureInfo.InvariantCulture)})";
            }
        }
    }

    private static Regex Key(string key) => new($@"(?<![A-Z_]){key}(?![A-Z_])", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"(^|\s)#.*$", RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    [GeneratedRegex(@"^\s*value:\s*(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex RenderValue();

    [GeneratedRegex(@"\d+(\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CreditDesk.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("CreditDesk.slnx not found above " + AppContext.BaseDirectory);
    }
}
