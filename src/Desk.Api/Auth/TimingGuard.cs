using System.Security.Cryptography;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Identity;

namespace Desk.Api.Auth;

/// <summary>Spends the same PBKDF2 work as a real password check, for login failures that never reach one.</summary>
internal static class TimingGuard
{
    private static readonly PasswordHasher<DeskUser> Hasher = new();
    private static readonly DeskUser Nobody = new();
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(Nobody, Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

    public static void Verify(string password) => Hasher.VerifyHashedPassword(Nobody, DummyHash.Value, password);
}
