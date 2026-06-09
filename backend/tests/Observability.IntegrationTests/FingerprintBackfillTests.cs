using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Observability.Api.Middleware;
using Observability.Application.Ingestion;
using Observability.Domain.Telemetry;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

public class FingerprintBackfillTests : IClassFixture<IngestionWebApplicationFactory>
{
    private readonly IngestionWebApplicationFactory _factory;

    public FingerprintBackfillTests(IngestionWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.SeedAsync().GetAwaiter().GetResult();
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminKeyAuthExtensions.HeaderName, _factory.AdminKeyPlaintext);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    [Fact]
    public async Task Backfill_restamps_stale_rows_merges_collisions_and_writes_audit()
    {
        var appId = _factory.SeededAppId;
        var envId = _factory.SeededEnvId;
        var now = DateTime.UtcNow;

        // Two rows that share the same fault inputs but carry distinct, pre-migration fingerprints
        // (as if produced by an older algorithm) — recompute collapses them onto one fingerprint, so
        // the second must MERGE into the first. A third row already has the correct fingerprint but a
        // stale version (re-stamped in place). A fourth is already current (untouched).
        var sharedInputs = ("PaymentDeclined", "Stripe.CardException", "checkout", (string?)null);
        var currentForShared = ErrorFingerprint.Compute(sharedInputs.Item1, sharedInputs.Item2, sharedInputs.Item3, sharedInputs.Item4);
        var inPlaceFp = ErrorFingerprint.Compute("DbTimeout", null, "orders", null);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();

            db.Errors.AddRange(
                new ErrorRecord // A: stale, legacy fingerprint, will be re-stamped to currentForShared
                {
                    ApplicationId = appId, EnvironmentId = envId,
                    Fingerprint = "legacyfingerprintaaaaaaaaaaaaaaaa", FingerprintVersion = 0,
                    ErrorType = sharedInputs.Item1, ExceptionType = sharedInputs.Item2, EndpointGroup = sharedInputs.Item3,
                    PropertiesJson = "{}", OccurrenceCount = 5,
                    FirstSeenAt = now.AddHours(-3), LastSeenAt = now.AddHours(-1),
                },
                new ErrorRecord // B: stale, different legacy fingerprint, same inputs -> merges into A
                {
                    ApplicationId = appId, EnvironmentId = envId,
                    Fingerprint = "legacyfingerprintbbbbbbbbbbbbbbbb", FingerprintVersion = 0,
                    ErrorType = sharedInputs.Item1, ExceptionType = sharedInputs.Item2, EndpointGroup = sharedInputs.Item3,
                    PropertiesJson = "{}", OccurrenceCount = 3,
                    FirstSeenAt = now.AddHours(-5), LastSeenAt = now.AddMinutes(-10),
                },
                new ErrorRecord // C: stale version but fingerprint already correct -> re-stamped in place
                {
                    ApplicationId = appId, EnvironmentId = envId,
                    Fingerprint = inPlaceFp, FingerprintVersion = 0,
                    ErrorType = "DbTimeout", EndpointGroup = "orders",
                    PropertiesJson = "{}", OccurrenceCount = 2,
                    FirstSeenAt = now.AddHours(-2), LastSeenAt = now.AddMinutes(-30),
                },
                new ErrorRecord // D: already current -> not scanned
                {
                    ApplicationId = appId, EnvironmentId = envId,
                    Fingerprint = ErrorFingerprint.Compute("AlreadyCurrent", null, null, null),
                    FingerprintVersion = ErrorFingerprint.CurrentVersion,
                    ErrorType = "AlreadyCurrent",
                    PropertiesJson = "{}", OccurrenceCount = 1,
                    FirstSeenAt = now.AddHours(-1), LastSeenAt = now,
                });
            await db.SaveChangesAsync();
        }

        var resp = await AdminClient().PostAsJsonAsync("/api/admin/fingerprints/backfill", new { batch_size = 100 });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<BackfillResponse>();

        body!.Scanned.Should().Be(3);     // A, B, C (D already current)
        body.Updated.Should().Be(2);      // A re-stamped (new fp), C re-stamped (same fp)
        body.Merged.Should().Be(1);       // B folded into A
        body.TargetVersion.Should().Be(ErrorFingerprint.CurrentVersion);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();

            // No row left below the current version anywhere.
            (await db.Errors.AnyAsync(e => e.FingerprintVersion < ErrorFingerprint.CurrentVersion))
                .Should().BeFalse();

            // A and B collapsed to a single canonical row carrying the summed count and widened bounds.
            var merged = await db.Errors.SingleAsync(e =>
                e.ApplicationId == appId && e.EnvironmentId == envId && e.Fingerprint == currentForShared);
            merged.OccurrenceCount.Should().Be(8);
            merged.FirstSeenAt.Should().Be(now.AddHours(-5)); // widened to B's earlier first-seen

            (await db.AuditLogs.CountAsync(a => a.Action == "admin.fingerprint.backfilled"))
                .Should().BeGreaterThanOrEqualTo(1);
        }

        // Idempotent: a second run finds nothing to do.
        var second = await AdminClient().PostAsJsonAsync("/api/admin/fingerprints/backfill", new { batch_size = 100 });
        second.EnsureSuccessStatusCode();
        var secondBody = await second.Content.ReadFromJsonAsync<BackfillResponse>();
        secondBody!.Scanned.Should().Be(0);
    }

    private sealed record BackfillResponse(
        int Scanned,
        int Updated,
        int Merged,
        [property: System.Text.Json.Serialization.JsonPropertyName("target_version")] int TargetVersion);
}
