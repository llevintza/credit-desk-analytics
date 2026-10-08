using System.Buffers.Binary;
using System.Security.Cryptography;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Desk.Api.Auth;

/// <summary>
/// Spends the same PBKDF2 work as a real password check, for login failures that never reach one (R105-F2):
/// an unknown email, an empty password, or a locked, disabled or expired account.
/// </summary>
/// <remarks>
/// It verifies through the app's own <see cref="IPasswordHasher{TUser}"/> against a decoy hash in Identity's v3
/// format with the configured iteration count, so it costs exactly what a wrong password for a real account costs.
/// The decoy is assembled from random bytes, not hashed, so building it costs no PBKDF2 of its own.
/// </remarks>
public sealed class TimingGuard(IPasswordHasher<DeskUser> hasher, IOptions<PasswordHasherOptions> options)
{
    private const byte FormatV3 = 0x01;
    private const int SaltBytes = 16;     // PasswordHasher's v3 salt size
    private const int SubkeyBytes = 32;   // PasswordHasher's v3 subkey size
    private const uint HmacSha512 = 2;    // KeyDerivationPrf.HMACSHA512, PasswordHasher's v3 default

    private static readonly DeskUser Nobody = new();

    private readonly Lazy<string> _decoy = new(() => DecoyHash(options.Value.IterationCount));

    /// <summary>One PBKDF2 verification that always fails.</summary>
    public void Verify(string password) => hasher.VerifyHashedPassword(Nobody, _decoy.Value, password);

    internal static string DecoyHash(int iterations)
    {
        var bytes = new byte[13 + SaltBytes + SubkeyBytes];
        bytes[0] = FormatV3;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), HmacSha512);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5), (uint)iterations);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(9), SaltBytes);
        RandomNumberGenerator.Fill(bytes.AsSpan(13));
        return Convert.ToBase64String(bytes);
    }
}
