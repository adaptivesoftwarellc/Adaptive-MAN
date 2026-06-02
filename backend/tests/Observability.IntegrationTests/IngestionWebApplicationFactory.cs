using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Observability.Domain.Applications;
using Observability.Infrastructure.Authentication;
using Observability.Infrastructure.Persistence;

namespace Observability.IntegrationTests;

public sealed class IngestionWebApplicationFactory : WebApplicationFactory<Program>
{
    public Guid SeededAppId { get; } = Guid.NewGuid();
    public Guid SeededEnvId { get; } = Guid.NewGuid();
    public string PublicKeyPlaintext { get; } = "aopub_test_public_key_xxxxxxxxxxxxxxxx";
    public string ServerKeyPlaintext { get; } = "aoserv_test_server_key_xxxxxxxxxxxxxxxx";
    public string RevokedKeyPlaintext { get; } = "aoserv_revoked_key_xxxxxxxxxxxxxxxxxxxx";
    public string AdminKeyPlaintext { get; } = "test-admin-key-xxxxxxxxxxxxxxxxxxxx";

    // Second tenant — used by the multi-tenant isolation regression tests (Issue 10.1).
    public Guid SecondAppId { get; } = Guid.NewGuid();
    public Guid SecondEnvId { get; } = Guid.NewGuid();
    public string SecondServerKeyPlaintext { get; } = "aoserv_test_tenant_b_key_xxxxxxxxxxxx";

    private readonly string _dbName = $"obs-test-{Guid.NewGuid():N}";
    private int _seeded;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ObservabilityDb"] = "InMemory",
                ["Observability:ApiKeyHashPepper"] = "test-pepper",
                ["Observability:AdminApiKey"] = AdminKeyPlaintext,
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace DbContext with InMemory for integration tests.
            var descriptor = services.Single(d => d.ServiceType == typeof(DbContextOptions<ObservabilityDbContext>));
            services.Remove(descriptor);
            services.AddDbContext<ObservabilityDbContext>(opt => opt.UseInMemoryDatabase(_dbName));
        });
    }

    public async Task SeedAsync()
    {
        if (Interlocked.Exchange(ref _seeded, 1) == 1) return;
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IApiKeyHasher>();

        await db.Database.EnsureCreatedAsync();

        db.Applications.Add(new Observability.Domain.Applications.Application
        {
            Id = SeededAppId,
            Name = "Test App",
            Slug = "test-app",
        });

        db.AppEnvironments.Add(new AppEnvironment
        {
            Id = SeededEnvId,
            ApplicationId = SeededAppId,
            EnvironmentName = "Development",
        });

        db.ApiKeys.AddRange(
            new ApiKey
            {
                ApplicationId = SeededAppId,
                EnvironmentId = SeededEnvId,
                KeyHash = hasher.Hash(PublicKeyPlaintext),
                KeyType = ApiKeyType.PublicClient,
            },
            new ApiKey
            {
                ApplicationId = SeededAppId,
                EnvironmentId = SeededEnvId,
                KeyHash = hasher.Hash(ServerKeyPlaintext),
                KeyType = ApiKeyType.ServerApi,
            },
            new ApiKey
            {
                ApplicationId = SeededAppId,
                EnvironmentId = SeededEnvId,
                KeyHash = hasher.Hash(RevokedKeyPlaintext),
                KeyType = ApiKeyType.ServerApi,
                RevokedAt = DateTime.UtcNow.AddDays(-1),
            });

        // Second tenant (App B) — its own app, environment, and server key. Lets the isolation
        // tests prove tenant A's key can never write or read tenant B's data (Issue 10.1).
        db.Applications.Add(new Observability.Domain.Applications.Application
        {
            Id = SecondAppId,
            Name = "Second App",
            Slug = "second-app",
        });

        db.AppEnvironments.Add(new AppEnvironment
        {
            Id = SecondEnvId,
            ApplicationId = SecondAppId,
            EnvironmentName = "Production",
        });

        db.ApiKeys.Add(new ApiKey
        {
            ApplicationId = SecondAppId,
            EnvironmentId = SecondEnvId,
            KeyHash = hasher.Hash(SecondServerKeyPlaintext),
            KeyType = ApiKeyType.ServerApi,
        });

        await db.SaveChangesAsync();
    }
}
