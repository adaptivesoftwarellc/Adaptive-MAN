using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Observability.Application.Ingestion;
using Observability.Application.Retention;
using Observability.Infrastructure.Authentication;
using Observability.Infrastructure.Persistence;

namespace Observability.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddObservabilityInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("ObservabilityDb")
            ?? throw new InvalidOperationException("ConnectionStrings:ObservabilityDb is not configured.");

        services.AddDbContext<ObservabilityDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));

        services.Configure<ApiKeyHasherOptions>(opts =>
        {
            opts.Pepper = configuration["Observability:ApiKeyHashPepper"] ?? string.Empty;
        });

        services.AddSingleton<IApiKeyHasher, ApiKeyHasher>();
        services.AddSingleton<IApiKeyGenerator, ApiKeyGenerator>();
        services.AddScoped<IApiKeyResolver, ApiKeyResolver>();

        // Issue 8.6 RBAC — local-user identity. PasswordHasher/AccessTokenService are stateless
        // singletons; the authenticator is scoped because it reads the request-scoped DbContext.
        services.Configure<AccessTokenOptions>(opts =>
        {
            opts.SigningKey = configuration["Observability:JwtSigningKey"] ?? string.Empty;
            if (int.TryParse(configuration["Observability:AccessTokenLifetimeMinutes"], out var minutes) && minutes > 0)
                opts.LifetimeMinutes = minutes;
        });
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IAccessTokenService, AccessTokenService>();
        services.AddScoped<IUserAuthenticator, LocalUserAuthenticator>();
        services.AddScoped<IIngestionStore, IngestionStore>();
        services.AddScoped<IErrorFingerprintBackfiller, ErrorFingerprintBackfiller>();

        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
        services.AddScoped<IRetentionSweeper, RetentionSweeper>();
        services.AddSingleton<IPropertyAllowlistValidator, PropertyAllowlistValidator>();
        services.AddScoped<IIngestionService, IngestionService>();

        return services;
    }
}
