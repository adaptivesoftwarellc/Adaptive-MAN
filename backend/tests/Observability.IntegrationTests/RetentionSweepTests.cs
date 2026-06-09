using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Observability.Application.Retention;
using Observability.Domain.Applications;
using Observability.Domain.Audit;
using Observability.Domain.Telemetry;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

// Own factory per test (not a shared IClassFixture) so each gets an isolated InMemory database —
// the sweep mutates global state, so cross-test bleed would be flaky.
public class RetentionSweepTests : IAsyncLifetime, IDisposable
{
    private readonly IngestionWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.SeedAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _factory.Dispose();

    private async Task<RetentionSweepResult> RunSweepAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var sweeper = scope.ServiceProvider.GetRequiredService<IRetentionSweeper>();
        return await sweeper.SweepAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_deletes_only_past_retention_rows_and_writes_audit()
    {
        var appId = _factory.SeededAppId;
        var envId = _factory.SeededEnvId;
        var now = DateTime.UtcNow;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();

            // Defaults: 90d events, 180d errors, 365d audit.
            db.Events.AddRange(
                MakeEvent(appId, envId, "old_event", now.AddDays(-100)),  // > 90d -> swept
                MakeEvent(appId, envId, "recent_event", now.AddDays(-5))); // <= 90d -> kept
            db.Errors.AddRange(
                MakeError(appId, envId, "OldError", now.AddDays(-200)),  // > 180d -> swept
                MakeError(appId, envId, "RecentError", now.AddDays(-10))); // <= 180d -> kept
            db.AuditLogs.AddRange(
                new AuditLog { Action = "admin.app.created", ActorType = "admin_key", OccurredAt = now.AddDays(-400) }, // > 365d -> swept
                new AuditLog { Action = "admin.app.created", ActorType = "admin_key", OccurredAt = now.AddDays(-1) });  // kept
            await db.SaveChangesAsync();
        }

        var result = await RunSweepAsync();

        result.EventsDeleted.Should().Be(1);
        result.ErrorsDeleted.Should().Be(1);
        result.AuditLogsDeleted.Should().Be(1);
        result.EnvironmentsSwept.Should().Be(2); // seeded main + second-tenant env

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();

            (await db.Events.Select(e => e.EventName).ToListAsync())
                .Should().ContainSingle().Which.Should().Be("recent_event");
            (await db.Errors.Select(e => e.ErrorType).ToListAsync())
                .Should().ContainSingle().Which.Should().Be("RecentError");

            // The old admin row is gone; the recent one plus the sweep's own audit row remain.
            (await db.AuditLogs.CountAsync(a => a.Action == "admin.retention.swept")).Should().Be(1);
            (await db.AuditLogs.CountAsync(a => a.Action == "admin.app.created")).Should().Be(1);
        }
    }

    [Fact]
    public async Task Per_env_override_tightens_retention()
    {
        var appId = Guid.NewGuid();
        var envId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
            db.Applications.Add(new Observability.Domain.Applications.Application { Id = appId, Name = "Short Retention", Slug = "short-retention" });
            db.AppEnvironments.Add(new AppEnvironment
            {
                Id = envId,
                ApplicationId = appId,
                EnvironmentName = "Development",
                EventRetentionDays = 7, // far tighter than the 90d default
            });
            db.Events.AddRange(
                MakeEvent(appId, envId, "over_override", now.AddDays(-10)), // > 7d -> swept under override (would survive default)
                MakeEvent(appId, envId, "under_override", now.AddDays(-2))); // <= 7d -> kept
            await db.SaveChangesAsync();
        }

        await RunSweepAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
            var remaining = await db.Events.Where(e => e.ApplicationId == appId).Select(e => e.EventName).ToListAsync();
            remaining.Should().ContainSingle().Which.Should().Be("under_override");
        }
    }

    private static EventRecord MakeEvent(Guid appId, Guid envId, string name, DateTime createdAt) => new()
    {
        ApplicationId = appId,
        EnvironmentId = envId,
        EventName = name,
        DistinctId = "u_test",
        PropertiesJson = "{}",
        OccurredAt = createdAt,
        CreatedAt = createdAt,
    };

    private static ErrorRecord MakeError(Guid appId, Guid envId, string errorType, DateTime lastSeenAt) => new()
    {
        ApplicationId = appId,
        EnvironmentId = envId,
        Fingerprint = Guid.NewGuid().ToString("N")[..32],
        ErrorType = errorType,
        PropertiesJson = "{}",
        FirstSeenAt = lastSeenAt.AddMinutes(-1),
        LastSeenAt = lastSeenAt,
    };
}
