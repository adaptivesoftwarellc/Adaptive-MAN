using System.Security.Cryptography;

namespace Observability.Infrastructure.Authentication;

public interface IPasswordHasher
{
    /// <summary>Produces a self-describing encoded hash safe to persist.</summary>
    string Hash(string password);

    /// <summary>Constant-time verification of <paramref name="password"/> against an encoded hash.</summary>
    bool Verify(string password, string encoded);
}

/// <summary>
/// Issue 8.6 — PBKDF2 (SHA-256) password hashing. Self-contained on <c>System.Security.Cryptography</c>
/// to avoid a new dependency; encoded form is <c>pbkdf2$&lt;iterations&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>
/// so the work factor can be raised later without breaking existing hashes.
/// </summary>
public sealed class PasswordHasher : IPasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public string Hash(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password must not be empty.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded)) return false;

        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
