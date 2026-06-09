using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Observability.Domain.Applications;
using Observability.Infrastructure.Authentication;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

public class BackgroundJobDedupTests : IClassFixture<IngestionWebApplicationFactory>
{
    private readonly IngestionWebApplicationFactory _factory;

    public BackgroundJobDedupTests(IngestionWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Hundred_identical_failures_collapse_to_one_incident_with_count_100()
    {
        await _factory.SeedAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Observability-Key", _factory.ServerKeyPlaintext);

        var payload = new
        {
            error_type = "TimeoutException",
            distinct_id = "system:background-service",
            occurred_at = DateTime.UtcNow,
            properties = new Dictionary<string, object?>
            {
                ["job_name"] = "nightly-import",
                ["error_type"] = "TimeoutException",
            },
        };

        for (var i = 0; i < 100; i++)
        {
            var res = await client.PostAsJsonAsync("/api/ingest/errors", payload);
            res.EnsureSuccessStatusCode();
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var rows = await db.BackgroundJobFailures
            .Where(b => b.ApplicationId == _factory.SeededAppId)
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal(100, rows[0].OccurrenceCount);
        Assert.Equal("nightly-import", rows[0].JobName);
        Assert.Equal("TimeoutException", rows[0].ErrorType);
        Assert.NotNull(rows[0].LastSuppressedAt);
        // First occurrence creates the incident; the other 99 land inside the default window and
        // are counted as suppressed (Issue 8.2).
        Assert.Equal(99, rows[0].SuppressedCount);
    }

    [Fact]
    public async Task Per_app_window_override_changes_suppression()
    {
        await _factory.SeedAsync();

        // A dedicated app/env with a 1-minute dedup window override.
        var appId = Guid.NewGuid();
        var envId = Guid.NewGuid();
        const string serverKey = "aoserv_bgoverride_key_xxxxxxxxxxxxxxxx";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IApiKeyHasher>();
            db.Applications.Add(new Observability.Domain.Applications.Application { Id = appId, Name = "BG Override", Slug = "bg-override" });
            db.AppEnvironments.Add(new AppEnvironment
            {
                Id = envId,
                ApplicationId = appId,
                EnvironmentName = "Development",
                BackgroundJobDedupWindowMinutes = 1,
            });
            db.ApiKeys.Add(new ApiKey
            {
                ApplicationId = appId,
                EnvironmentId = envId,
                KeyHash = hasher.Hash(serverKey),
                KeyType = ApiKeyType.ServerApi,
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Observability-Key", serverKey);

        // Three failures spaced 5 minutes apart. Under the default 15-minute window all three would
        // dedup-suppress; with the 1-minute override none of the gaps fall inside the window.
        var baseTime = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 3; i++)
        {
            var res = await client.PostAsJsonAsync("/api/ingest/errors", new
            {
                error_type = "TimeoutException",
                distinct_id = "system:background-service",
                occurred_at = baseTime.AddMinutes(i * 5),
                properties = new Dictionary<string, object?>
                {
                    ["job_name"] = "spaced-import",
                    ["error_type"] = "TimeoutException",
                },
            });
            res.EnsureSuccessStatusCode();
        }

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var row = await vdb.BackgroundJobFailures.SingleAsync(b => b.ApplicationId == appId);

        Assert.Equal(3, row.OccurrenceCount);
        Assert.Equal(0, row.SuppressedCount); // none within the 1-minute override window
    }
}
