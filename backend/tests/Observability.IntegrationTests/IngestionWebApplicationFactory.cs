using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Observability.Domain.Applications;
using Observability.Domain.Identity;
using Observability.Infrastructure.Authentication;
using Observability.Infrastructure.Persistence;

namespace Observability.IntegrationTests;

public class IngestionWebApplicationFactory : WebApplicationFactory<Program>
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

    // Issue 8.6 RBAC users. Admin is a global reader; AppOwner is scoped to tenant A only.
    public string AdminEmail { get; } = "admin@test.local";
    public string AdminPassword { get; } = "admin-pass-123";
    public string AppOwnerEmail { get; } = "owner-a@test.local";
    public string AppOwnerPassword { get; } = "owner-pass-123";

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
                ["Observability:JwtSigningKey"] = "test-jwt-signing-key-0123456789abcdef0123456789",
                // Issue 10.8 — keep the dogfood SDK inert by default so tests don't attempt self-ingest
                // over HTTP. The dogfood test overrides this and supplies a recording IAnalyticsService.
                ["AdaptiveObservability:Enabled"] = "false",
                // Booting the API host also starts the background hosts (retention sweep, alert
                // evaluator). Keep both off so they can't run a startup sweep or DB poll mid-test.
                ["Observability:Retention:Enabled"] = "false",
                ["Observability:Alerting:Enabled"] = "false",
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

        // Issue 8.6 — seed RBAC users: a global-read Admin and an AppOwner scoped to tenant A.
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        db.Users.Add(new User
        {
            Email = AdminEmail,
            DisplayName = "Test Admin",
            PasswordHash = passwordHasher.Hash(AdminPassword),
            Role = Role.Admin,
        });
        var owner = new User
        {
            Email = AppOwnerEmail,
            DisplayName = "Tenant A Owner",
            PasswordHash = passwordHasher.Hash(AppOwnerPassword),
            Role = Role.AppOwner,
        };
        db.Users.Add(owner);
        db.UserApplicationAssignments.Add(new UserApplicationAssignment
        {
            UserId = owner.Id,
            ApplicationId = SeededAppId,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Logs in via the real auth endpoint and returns the bearer token.</summary>
    public async Task<string> LoginAsync(string email, string password)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>An HttpClient pre-authenticated with a bearer token for the given user.</summary>
    public async Task<HttpClient> BearerClientAsync(string email, string password)
    {
        var token = await LoginAsync(email, password);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
