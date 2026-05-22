using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Observability.Api.Configuration;

/// <summary>
/// Wires the Azure Key Vault configuration provider for non-Development environments and validates
/// that all secrets required by the platform are present. Required-secret list and naming come from
/// DEVELOPMENT_PLAN.md Phase 2 / docs/azure-key-vault-setup.md.
/// </summary>
public static class KeyVaultConfiguration
{
    private const string KeyVaultUriConfigKey = "KeyVault:Uri";

    public static readonly IReadOnlyList<RequiredSecret> RequiredSecrets = new List<RequiredSecret>
    {
        new("ObservabilityDbConnection", "ConnectionStrings:ObservabilityDb"),
        new("ApiKeyHashPepper",          "Observability:ApiKeyHashPepper"),
        new("JwtSigningKey",             "Observability:JwtSigningKey"),
        new("EncryptionKey",             "Observability:EncryptionKey"),
        new("ObservabilityAdminKey",     "Observability:AdminApiKey"),
    };

    public static void AddKeyVaultIfConfigured(this WebApplicationBuilder builder)
    {
        var vaultUri = builder.Configuration[KeyVaultUriConfigKey];
        var isDev = builder.Environment.IsDevelopment();

        if (string.IsNullOrWhiteSpace(vaultUri))
        {
            if (isDev) return;
            throw new InvalidOperationException(
                $"Azure Key Vault URI is required in {builder.Environment.EnvironmentName}. " +
                $"Set '{KeyVaultUriConfigKey}' (e.g. https://kv-observability-uat.vault.azure.net/).");
        }

        var credential = new DefaultAzureCredential();
        var client = new SecretClient(new Uri(vaultUri), credential);

        builder.Configuration.AddAzureKeyVault(
            client,
            new ObservabilitySecretManager());
    }

    /// <summary>
    /// Verifies every required secret is bound. In non-Development this throws — fail-fast at startup
    /// is preferable to discovering a missing secret on the first ingestion request.
    /// </summary>
    public static void ValidateRequiredSecrets(this WebApplication app)
    {
        var missing = new List<string>();
        foreach (var secret in RequiredSecrets)
        {
            var value = app.Configuration[secret.ConfigKey];
            if (string.IsNullOrWhiteSpace(value)) missing.Add($"{secret.SecretName} -> {secret.ConfigKey}");
        }

        if (missing.Count == 0) return;

        var message = "Required configuration is missing: " + string.Join(", ", missing);
        if (app.Environment.IsDevelopment())
        {
            app.Logger.LogWarning("{Message}. Continuing because environment is Development.", message);
            return;
        }

        app.Logger.LogCritical("{Message}. Refusing to start.", message);
        throw new InvalidOperationException(message);
    }

    public sealed record RequiredSecret(string SecretName, string ConfigKey);

    private sealed class ObservabilitySecretManager : KeyVaultSecretManager
    {
        public override bool Load(SecretProperties secret) => secret.Enabled.GetValueOrDefault(true);

        public override string GetKey(KeyVaultSecret secret)
        {
            foreach (var required in RequiredSecrets)
            {
                if (string.Equals(secret.Name, required.SecretName, StringComparison.OrdinalIgnoreCase))
                    return required.ConfigKey;
            }
            // Default Key Vault convention: "Section--Key" -> "Section:Key".
            return secret.Name.Replace("--", ":");
        }
    }
}
