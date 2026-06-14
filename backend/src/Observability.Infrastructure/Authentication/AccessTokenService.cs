using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Observability.Domain.Identity;

namespace Observability.Infrastructure.Authentication;

public sealed class AccessTokenOptions
{
    /// <summary>HMAC signing key. Bound from <c>Observability:JwtSigningKey</c> (Key Vault in deployed envs).</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int LifetimeMinutes { get; set; } = 480;
}

/// <summary>Claims carried by a validated token. Owned-app resolution happens at the auth layer, not here.</summary>
public sealed record TokenClaims(Guid UserId, string Email, Role Role);

public interface IAccessTokenService
{
    (string Token, DateTime ExpiresAt) Issue(User user);

    /// <summary>Returns the claims if the signature and expiry are valid; otherwise null. Never throws on bad input.</summary>
    TokenClaims? Validate(string token);
}

/// <summary>
/// Issue 8.6 — self-contained HMAC-SHA256 bearer tokens (JWT-shaped: <c>header.payload.signature</c>,
/// base64url). Deliberately hand-rolled on <c>System.Security.Cryptography</c> rather than pulling a JWT
/// package, matching the existing <see cref="ApiKeyHasher"/> approach and the no-new-dependency
/// constraint. This is the local-users implementation of the identity seam; an Entra/JwtBearer adapter
/// can replace it behind <see cref="IUserAuthenticator"/> without touching callers.
/// </summary>
public sealed class AccessTokenService : IAccessTokenService
{
    private const int MinSigningKeyBytes = 32;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly byte[] _signingKey;
    private readonly int _lifetimeMinutes;

    public AccessTokenService(IOptions<AccessTokenOptions> options)
    {
        var key = options.Value.SigningKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "Observability:JwtSigningKey is not configured. Set it via Key Vault (deployed) or appsettings.Development.json (local).");
        }
        _signingKey = Encoding.UTF8.GetBytes(key);
        if (_signingKey.Length < MinSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"Observability:JwtSigningKey is too short ({_signingKey.Length} bytes). HMAC-SHA256 requires at least {MinSigningKeyBytes} bytes of key material.");
        }
        _lifetimeMinutes = options.Value.LifetimeMinutes > 0 ? options.Value.LifetimeMinutes : 480;
    }

    private sealed record Header(string alg, string typ);
    private sealed record Payload(string sub, string email, int role, long iat, long exp);

    public (string Token, DateTime ExpiresAt) Issue(User user)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(_lifetimeMinutes);

        var header = Encode(new Header("HS256", "AO"));
        var payload = Encode(new Payload(
            user.Id.ToString("N"),
            user.Email,
            (int)user.Role,
            now.ToUnixTimeSeconds(),
            expires.ToUnixTimeSeconds()));

        var signature = Sign($"{header}.{payload}");
        return ($"{header}.{payload}.{signature}", expires.UtcDateTime);
    }

    public TokenClaims? Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        // Compare raw HMAC bytes, not the base64url strings, so the constant-time guarantee holds and no
        // length is leaked through the string encoding.
        var expectedSig = SignBytes($"{parts[0]}.{parts[1]}");
        byte[] providedSig;
        try
        {
            providedSig = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return null;
        }
        if (!CryptographicOperations.FixedTimeEquals(providedSig, expectedSig)) return null;

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(Base64UrlDecode(parts[1]), Json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
        if (payload is null) return null;

        if (DateTimeOffset.FromUnixTimeSeconds(payload.exp) <= DateTimeOffset.UtcNow) return null;
        if (!Guid.TryParse(payload.sub, out var userId)) return null;
        if (!Enum.IsDefined(typeof(Role), payload.role)) return null;

        return new TokenClaims(userId, payload.email, (Role)payload.role);
    }

    private byte[] SignBytes(string data) => HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(data));

    private string Sign(string data) => Base64UrlEncode(SignBytes(data));

    private static string Encode<T>(T value) =>
        Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
