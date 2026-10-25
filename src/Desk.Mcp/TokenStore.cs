using System.Text.Json;

namespace Desk.Mcp;

/// <summary>What the token file holds (ADR-0031 §2): the origin and the refresh token, never the email or the password.</summary>
internal sealed record SavedSignIn(string Origin, string RefreshToken);

/// <summary>
/// One 0600 file per origin in the user's config directory (<c>$XDG_CONFIG_HOME/desk-mcp/</c>, else
/// <c>~/.config/desk-mcp/</c>; <c>%APPDATA%\desk-mcp\</c> on Windows), replaced by write-temp-then-rename. A lock file
/// next to it serialises refreshes, so two <c>serve</c> processes never present the same refresh token twice (#353).
/// </summary>
internal sealed class TokenStore
{
    private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode UserOnlyDirectory = UserOnlyFile | UnixFileMode.UserExecute;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public TokenStore(string directory, ApiOrigin origin)
    {
        Directory = directory;
        Origin = origin;
        var name = string.Concat(origin.Value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? char.ToLowerInvariant(c) : '_'));
        FilePath = Path.Combine(directory, name + ".json");
    }

    public string Directory { get; }
    public ApiOrigin Origin { get; }
    public string FilePath { get; }

    /// <summary>The config directory from the environment, or null when there is no home to put it in.</summary>
    public static string? ConfigDirectory(Func<string, string?> env)
    {
        string? root;
        if (OperatingSystem.IsWindows())
            root = env("APPDATA");
        else
        {
            var xdg = env("XDG_CONFIG_HOME");
            var home = env("HOME");
            root = !string.IsNullOrEmpty(xdg) && Path.IsPathFullyQualified(xdg) ? xdg
                : !string.IsNullOrEmpty(home) ? Path.Combine(home, ".config") : null;
        }
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "desk-mcp");
    }

    public SavedSignIn? Read()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedSignIn>(File.ReadAllText(FilePath), Json);
            return saved is { RefreshToken.Length: > 0 } && saved.Origin == Origin.Value ? saved : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Write(string refreshToken)
    {
        EnsureDirectory();
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UserOnlyFile;
        try
        {
            using (var stream = new FileStream(temp, options))
                JsonSerializer.Serialize(stream, new SavedSignIn(Origin.Value, refreshToken), Json);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public void Delete() => File.Delete(FilePath);

    /// <summary>Holds the origin's lock file exclusively until disposed; waits for another process that holds it.</summary>
    public async Task<IDisposable> LockAsync(TimeSpan timeout, CancellationToken ct)
    {
        EnsureDirectory();
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UserOnlyFile;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(FilePath + ".lock", options);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            }
        }
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
            System.IO.Directory.CreateDirectory(Directory);
        else
            System.IO.Directory.CreateDirectory(Directory, UserOnlyDirectory);
    }
}
