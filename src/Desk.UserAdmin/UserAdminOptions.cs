using System.Globalization;
using Desk.Data.Auth;

namespace Desk.UserAdmin;

public enum UserAdminCommand { Add, List, Disable, Reset, Grant, Revoke }

/// <summary>Parsed command line. Throws <see cref="ArgumentException"/> with a usage-ready message on bad input.</summary>
public sealed record UserAdminOptions(UserAdminCommand Command, string? Email, string? Role, DateOnly? Expires, IReadOnlyList<int>? Portfolios = null)
{
    public const string Usage = """
        Usage:
          Desk.UserAdmin add     --email <email> --role viewer|admin --expires YYYY-MM-DD
          Desk.UserAdmin list
          Desk.UserAdmin disable --email <email>
          Desk.UserAdmin reset   --email <email>
          Desk.UserAdmin grant   --email <email> --portfolio <id>[,<id>...]
          Desk.UserAdmin revoke  --email <email> --portfolio <id>[,<id>...]
        Connection: ConnectionStrings__App or DATABASE_URL. The account is valid through the --expires date (UTC).
        Grants (ADR-0021) apply at the user's next token refresh, within 5 minutes, and never sign the user out.
        """;

    public static UserAdminOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            throw new ArgumentException("No command given.");

        var command = args[0].ToLowerInvariant() switch
        {
            "add" => UserAdminCommand.Add,
            "list" => UserAdminCommand.List,
            "disable" => UserAdminCommand.Disable,
            "reset" => UserAdminCommand.Reset,
            "grant" => UserAdminCommand.Grant,
            "revoke" => UserAdminCommand.Revoke,
            _ => throw new ArgumentException($"Unknown command '{args[0]}'."),
        };

        string? email = null, role = null;
        DateOnly? expires = null;
        int[]? portfolios = null;
        for (var i = 1; i < args.Count; i++)
        {
            var name = args[i];
            if (i + 1 >= args.Count)
                throw new ArgumentException($"Option {name} needs a value.");
            var value = args[++i];
            switch (name)
            {
                case "--email": email = value.Trim(); break;
                case "--role": role = value.Trim().ToLowerInvariant(); break;
                case "--expires":
                    expires = DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                        ? d
                        : throw new ArgumentException($"--expires must be YYYY-MM-DD (got '{value}').");
                    break;
                case "--portfolio" when command is UserAdminCommand.Grant or UserAdminCommand.Revoke:
                    portfolios = ParsePortfolios(value);
                    break;
                default: throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        if (command != UserAdminCommand.List && string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("--email is required.");
        if (command == UserAdminCommand.Add)
        {
            if (!Roles.IsKnown(role)) throw new ArgumentException("--role must be viewer or admin.");
            if (expires is null) throw new ArgumentException("--expires is required.");
        }
        if (command is UserAdminCommand.Grant or UserAdminCommand.Revoke && portfolios is null)
            throw new ArgumentException("--portfolio is required.");
        return new UserAdminOptions(command, email, role, expires, portfolios);
    }

    /// <summary>Comma-separated positive portfolio ids, sorted and de-duplicated.</summary>
    private static int[] ParsePortfolios(string value)
    {
        var ids = new SortedSet<int>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new ArgumentException($"--portfolio takes comma-separated positive portfolio ids (got '{value}').");
            ids.Add(id);
        }
        return [.. ids];
    }

    /// <summary>The account works through the whole <c>--expires</c> day: it expires at the next UTC midnight.</summary>
    public static DateTimeOffset ExpiryInstant(DateOnly date) =>
        new(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
