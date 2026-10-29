using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Desk.Mcp.Tests;

/// <summary>Origins (ADR-0031 §2, D17): http only on loopback, --live for anything that isn't a local stack.</summary>
public sealed class ApiOriginTests
{
    [Theory]
    [InlineData("http://localhost:8080", false, "http://localhost:8080")]
    [InlineData("http://127.0.0.1:5180", false, "http://127.0.0.1:5180")]
    [InlineData("http://[::1]:8080", false, "http://[::1]:8080")]
    [InlineData("https://localhost", false, "https://localhost")]
    [InlineData("https://app:8443", false, "https://app:8443")]
    [InlineData("https://desk.example/", true, "https://desk.example")]
    [InlineData("  https://DESK.example  ", true, "https://desk.example")]
    public void Accepted(string text, bool live, string expected)
    {
        var origin = ApiOrigin.TryParse(text, live, out var error);
        Assert.Null(error);
        Assert.Equal(expected, origin!.ToString());
    }

    [Theory]
    [InlineData("http://desk.example", true, "http:// is allowed only for localhost")]
    [InlineData("http://app:8080", false, "http:// is allowed only for localhost")]
    [InlineData("https://desk.example", false, "also needs --live")]
    [InlineData("https://desk.example/api", true, "origin only")]
    [InlineData("https://desk.example?x=1", true, "origin only")]
    [InlineData("https://user@desk.example", true, "origin only")]
    [InlineData("ftp://localhost", false, "needs an origin")]
    [InlineData("localhost:8080", false, "needs an origin")]
    [InlineData("", false, "needs an origin")]
    [InlineData(null, false, "needs an origin")]
    public void Refused(string? text, bool live, string message)
    {
        Assert.Null(ApiOrigin.TryParse(text, live, out var error));
        Assert.Contains(message, error);
    }
}

public sealed class TokenStoreTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly ConfigHome _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void The_config_directory_follows_XDG_then_HOME()
    {
        Assert.Equal("/x/desk-mcp", TokenStore.ConfigDirectory(n => n == "XDG_CONFIG_HOME" ? "/x" : "/home/u"));
        Assert.Equal("/home/u/.config/desk-mcp", TokenStore.ConfigDirectory(n => n == "HOME" ? "/home/u" : null));
        // A relative XDG_CONFIG_HOME is ignored, as the XDG spec says.
        Assert.Equal("/home/u/.config/desk-mcp", TokenStore.ConfigDirectory(n => n == "XDG_CONFIG_HOME" ? "rel" : "/home/u"));
        Assert.Null(TokenStore.ConfigDirectory(_ => null));
    }

    [Fact]
    public void One_file_per_origin_named_after_it()
    {
        Assert.EndsWith("http___127.0.0.1_8080.json", _home.Store("http://127.0.0.1:8080").FilePath);
        Assert.EndsWith("http______1__8080.json", _home.Store("http://[::1]:8080").FilePath);
        Assert.NotEqual(_home.Store("http://localhost:1").FilePath, _home.Store("http://localhost:2").FilePath);
    }

    [Fact]
    public void A_missing_corrupt_or_foreign_file_reads_as_not_signed_in()
    {
        var store = _home.Store();
        Assert.Null(store.Read());
        store.Write("t");
        Assert.Equal(new SavedSignIn("http://localhost", "t"), store.Read());
        File.WriteAllText(store.FilePath, "{not json");
        Assert.Null(store.Read());
        File.WriteAllText(store.FilePath, """{"origin":"https://elsewhere","refreshToken":"t"}""");
        Assert.Null(store.Read());
        File.WriteAllText(store.FilePath, """{"origin":"http://localhost","refreshToken":""}""");
        Assert.Null(store.Read());
        store.Delete();
        store.Delete(); // deleting twice is fine
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void A_rewrite_replaces_the_file_and_stays_0600()
    {
        var store = _home.Store();
        store.Write("one");
        File.SetUnixFileMode(store.FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        store.Write("two");
        Assert.Equal("two", store.Read()!.RefreshToken);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
    }

    [Fact]
    public async Task The_lock_is_exclusive_and_a_waiter_gets_it_once_released()
    {
        var store = _home.Store();
        var first = await store.LockAsync(TimeSpan.Zero, Ct);
        await Assert.ThrowsAnyAsync<IOException>(() => store.LockAsync(TimeSpan.Zero, Ct));
        var waiter = store.LockAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(120, Ct);
        Assert.False(waiter.IsCompleted);
        first.Dispose();
        (await waiter).Dispose();
    }
}

public sealed class RequestGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_bucket_allows_30_at_once_then_one_every_2_s()
    {
        var time = new FakeTimeProvider();
        var gate = new RequestGate(time);
        for (var i = 0; i < RequestGate.PerMinute; i++) await gate.TakeAsync(Ct);
        var next = gate.TakeAsync(Ct);
        Assert.False(next.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1.9));
        await Task.Delay(20, Ct);
        Assert.False(next.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(0.1));
        await next.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        // A long idle refills to 30, never more.
        time.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < RequestGate.PerMinute; i++) await gate.TakeAsync(Ct);
        Assert.False(gate.TakeAsync(Ct).IsCompleted);
    }

    [Fact]
    public async Task One_call_at_a_time()
    {
        var gate = new RequestGate(TimeProvider.System);
        var first = await gate.EnterAsync(Ct);
        var second = gate.EnterAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(second.IsCompleted);
        first.Dispose();
        first.Dispose(); // idempotent: releases once
        using var _ = await second.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(gate.EnterAsync(Ct).IsCompleted);
    }
}

public sealed class TableResultTests
{
    private static List<JsonArray> Rows(int n, int width = 10) =>
        [.. Enumerable.Range(0, n).Select(i => new JsonArray(JsonValue.Create(i), JsonValue.Create(new string('x', width))))];

    [Fact]
    public void Within_the_caps_nothing_is_truncated()
    {
        var o = JsonDocument.Parse(TableResult.Build(["id", "v"], Rows(3), TableResult.MaxRows, new JsonObject { ["latest"] = "d" })).RootElement;
        Assert.Equal("d", o.GetProperty("latest").GetString());
        Assert.Equal(3, o.GetProperty("rows").GetArrayLength());
        Assert.Equal(3, o.GetProperty("rows_returned").GetInt32());
        Assert.Equal(3, o.GetProperty("rows_matched").GetInt32());
        Assert.False(o.GetProperty("truncated").GetBoolean());
        Assert.False(o.TryGetProperty("truncated_by", out _));
    }

    [Fact]
    public void The_row_cap_cuts_and_says_so_and_never_passes_500()
    {
        var o = JsonDocument.Parse(TableResult.Build(["id", "v"], Rows(150), TableResult.DefaultRows)).RootElement;
        Assert.Equal(100, o.GetProperty("rows_returned").GetInt32());
        Assert.Equal(150, o.GetProperty("rows_matched").GetInt32());
        Assert.Equal("rows", o.GetProperty("truncated_by").GetString());
        var max = JsonDocument.Parse(TableResult.Build(["id", "v"], Rows(600, 1), 10_000)).RootElement;
        Assert.Equal(TableResult.MaxRows, max.GetProperty("rows_returned").GetInt32());
    }

    [Fact]
    public void The_byte_cap_keeps_the_most_rows_that_fit_in_64_KB()
    {
        var text = TableResult.Build(["id", "v"], Rows(500, 400), TableResult.MaxRows);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= TableResult.MaxBytes);
        var o = JsonDocument.Parse(text).RootElement;
        var returned = o.GetProperty("rows_returned").GetInt32();
        Assert.InRange(returned, 100, 499);
        Assert.Equal("bytes", o.GetProperty("truncated_by").GetString());
        Assert.True(o.GetProperty("truncated").GetBoolean());
        // One more row would not have fit.
        var bigger = TableResult.Build(["id", "v"], Rows(returned + 1, 400), TableResult.MaxRows, maxBytes: int.MaxValue);
        Assert.True(Encoding.UTF8.GetByteCount(bigger) > TableResult.MaxBytes);
    }
}

public sealed class RedactTests
{
    [Theory]
    [InlineData("user a.b+c@example.co.uk failed", "user [email] failed")]
    [InlineData("plain 404 Not Found", "plain 404 Not Found")]
    [InlineData(null, "")]
    public void Scrubs_emails_tokens_and_passwords(string? text, string expected) => Assert.Equal(expected, Redact.Text(text));

    // Built at run time: no token-shaped or password literal sits in the repository (gitleaks, AGENTS.md hard rules).
    [Fact]
    public void Scrubs_generated_tokens_and_connection_string_passwords()
    {
        var token = $"{Base64Url(24)}.{Base64Url(48)}.{Base64Url(32)}";
        Assert.Equal("token [token] end", Redact.Text($"token {token} end"));
        var secret = Base64Url(9);
        var key = "Pass" + "word";
        Assert.Equal($"Host=h;{key}=[redacted];Database=d", Redact.Text($"Host=h;{key}={secret};Database=d"));
    }

    internal static string Base64Url(int bytes) =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class StderrLoggerTests
{
    [Fact]
    public void Lines_are_redacted_and_below_the_minimum_are_dropped()
    {
        var w = new StringWriter();
        using var provider = new StderrLoggerProvider(w, LogLevel.Information);
        var log = provider.CreateLogger("Desk.Mcp.DeskApiClient");
        log.LogDebug("hidden");
        log.LogWarning(new InvalidOperationException("for x@example.com"), "user {Email}", "x@example.com");
        Assert.Null(log.BeginScope("s"));
        Assert.False(log.IsEnabled(LogLevel.None));
        Assert.Equal("desk-mcp warning DeskApiClient: user [email] (InvalidOperationException: for [email])" + Environment.NewLine, w.ToString());
    }
}

public sealed class DeskMcpAppTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly ConfigHome _home = new();

    public void Dispose() => _home.Dispose();

    private async Task<(int Code, string Stderr)> RunAsync(Func<string, string?> env, params string[] args)
    {
        var w = new StringWriter();
        var code = await new DeskMcpApp(w, new FakePrompt("", ""), env, TimeProvider.System).RunAsync(args, Stream.Null, Stream.Null, Ct);
        return (code, w.ToString());
    }

    [Theory]
    [InlineData]
    [InlineData("help")]
    [InlineData("serve")]
    [InlineData("serve", "--api")]
    [InlineData("serve", "--api", "http://localhost", "--api", "http://localhost")]
    [InlineData("login", "--api", "http://localhost", "--email", "x")]
    [InlineData("login", "--password", "x", "--api", "http://localhost")]
    public async Task Bad_arguments_print_usage_and_exit_2(params string[] args)
    {
        var (code, stderr) = await RunAsync(_home.Env, args);
        Assert.Equal(2, code);
        Assert.Contains("Usage:", stderr);
    }

    [Fact]
    public async Task A_refused_origin_exits_2_before_any_request()
    {
        var (code, stderr) = await RunAsync(_home.Env, "serve", "--api", "http://desk.example");
        Assert.Equal(2, code);
        Assert.Contains("Refusing http://desk.example", stderr);
        (code, stderr) = await RunAsync(_home.Env, "login", "--api", "https://desk.example");
        Assert.Equal(2, code);
        Assert.Contains("--live", stderr);
    }

    [Fact]
    public async Task No_config_directory_exits_2()
    {
        var (code, stderr) = await RunAsync(_ => null, "serve", "--api", "http://localhost");
        Assert.Equal(2, code);
        Assert.Contains("No user config directory", stderr);
    }

    [Fact]
    public async Task Cancelling_serve_exits_cleanly()
    {
        using var cts = new CancellationTokenSource();
        var w = new StringWriter();
        var app = new DeskMcpApp(TextWriter.Synchronized(w), new FakePrompt("", ""), _home.Env, TimeProvider.System);
        var pipe = new System.IO.Pipelines.Pipe();
        var run = app.RunAsync(["serve", "--api", "http://localhost", "--live"], pipe.Reader.AsStream(), Stream.Null, cts.Token);
        await Task.Delay(200, Ct);
        await cts.CancelAsync();
        Assert.Contains(await run.WaitAsync(TimeSpan.FromSeconds(10), Ct), new[] { 0, 130 });
        Assert.Contains("serving http://localhost on stdio", w.ToString());
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("Connection refused");
        }
    }

    [Fact]
    public async Task An_unreachable_api_exits_1_and_a_cancelled_login_exits_130()
    {
        var w = new StringWriter();
        var app = new DeskMcpApp(w, new FakePrompt("e", "p"), _home.Env, TimeProvider.System, () => new Unreachable());
        Assert.Equal(1, await app.RunAsync(["login", "--api", "http://localhost"], Stream.Null, Stream.Null, Ct));
        Assert.Contains("Could not reach the desk API at http://localhost", w.ToString());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Equal(130, await app.RunAsync(["login", "--api", "http://localhost"], Stream.Null, Stream.Null, cancelled.Token));
    }

    [Fact]
    public void The_version_is_the_package_version() => Assert.Matches(@"^\d+\.\d+\.\d+", DeskMcpApp.Version);
}
