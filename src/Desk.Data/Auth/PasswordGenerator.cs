using System.Security.Cryptography;

namespace Desk.Data.Auth;

/// <summary>Strong random passwords for invited accounts. Always satisfies <see cref="IdentityPolicy"/>.</summary>
public static class PasswordGenerator
{
    public const int DefaultLength = 24;

    // No look-alikes (0/O, 1/l/I) so a password read aloud or retyped survives.
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!#$%&*+-=?@^_~";
    private const string All = Upper + Lower + Digits + Symbols;

    public static string Generate(int length = DefaultLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, IdentityPolicy.MinPasswordLength);
        var chars = new char[length];
        // One of each class, then fill and shuffle, so every class is present without a biased position.
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Symbols);
        for (var i = 4; i < length; i++) chars[i] = Pick(All);
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
}
