using System.Security.Cryptography;
using Observability.Domain.Applications;

namespace Observability.Infrastructure.Authentication;

public interface IApiKeyGenerator
{
    string Generate(ApiKeyType type);
}

public sealed class ApiKeyGenerator : IApiKeyGenerator
{
    private const int RandomBytes = 32;

    public string Generate(ApiKeyType type)
    {
        var prefix = type switch
        {
            ApiKeyType.PublicClient => "aopub_",
            ApiKeyType.ServerApi => "aoserv_",
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        var bytes = RandomNumberGenerator.GetBytes(RandomBytes);
        var body = Convert.ToBase64String(bytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "");
        return prefix + body;
    }
}
