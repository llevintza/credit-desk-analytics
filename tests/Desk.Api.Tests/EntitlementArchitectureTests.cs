using System.Text.RegularExpressions;

namespace Desk.Api.Tests;

/// <summary>
/// ADR-0021 R1: the unscoped portfolio list (<c>MetaSnapshot.UnscopedPortfolios</c>, <c>MetaRepository.PortfoliosAsync</c>)
/// is read only by the files allowlisted here. Anything else that returns portfolio-owned data resolves its scope through
/// <c>IPortfolioEntitlements</c> instead; a new reader needs an ADR-0021 amendment and an entry below.
/// </summary>
public sealed partial class EntitlementArchitectureTests
{
    /// <summary>Repo-relative path → why it may read the unscoped list.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Allowlist = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["src/Desk.Api/Positions/PortfolioEntitlements.cs"] = "the provider implementations",
        ["src/Desk.Api/Positions/MetaEndpoints.cs"] = "/api/meta/portfolios filters the list through IPortfolioEntitlements",
        ["src/Desk.Api/Positions/MetaCache.cs"] = "the loader that reads the list into MetaSnapshot",
        ["src/Desk.Data/Grid/MetaRepository.cs"] = "the definition",
        ["src/Desk.Api/Program.cs"] = "DI registration only",
        ["src/Desk.Api/Funds/FundEndpoints.cs"] = "all-or-nothing fund gate; reads the list only to find the fund's portfolios (ADR-0021 amendment 2026-10-09)",
    };

    [GeneratedRegex(@"\bUnscopedPortfolios\b|\bPortfoliosAsync\(|\bMetaRepository\b")]
    private static partial Regex UnscopedRead();

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "Desk.Api", "Desk.Api.csproj")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (src/Desk.Api/Desk.Api.csproj) not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The source files under src/ that read the unscoped list, repo-relative with forward slashes.</summary>
    internal static IReadOnlyList<string> Readers(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Where(f => UnscopedRead().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Only_allowlisted_files_read_the_unscoped_portfolio_list()
    {
        var offenders = Readers(RepoRoot()).Where(f => !Allowlist.ContainsKey(f)).ToList();
        Assert.True(offenders.Count == 0,
            "These files read the unscoped portfolio list (UnscopedPortfolios, PortfoliosAsync( or MetaRepository) outside the "
            + "ADR-0021 R1 allowlist. Resolve the portfolio scope through IPortfolioEntitlements and apply it in SQL instead, "
            + "or amend ADR-0021 and add the file to EntitlementArchitectureTests.Allowlist:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_allowlist_entry_exists_and_still_reads_the_list()
    {
        // A stale entry would let a later file of the same name in through the back door.
        var readers = Readers(RepoRoot());
        Assert.All(Allowlist.Keys, path => Assert.Contains(path, readers));
    }

    [Theory]
    [InlineData("var ids = meta.UnscopedPortfolios.Select(p => p.PortfolioId);", true)]
    [InlineData("var list = await repo.PortfoliosAsync(ct);", true)]
    [InlineData("public sealed class Foo(MetaRepository repo) { }", true)]
    [InlineData("var allowed = entitlements.For(http.User, meta);", false)]
    [InlineData("var x = meta.UnscopedPortfoliosCount;", false)]
    public void The_scan_matches_each_way_of_reading_the_list(string source, bool matches) =>
        Assert.Equal(matches, UnscopedRead().IsMatch(source));
}
