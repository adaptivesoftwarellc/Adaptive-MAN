using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Observability.Api.Middleware;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

/// <summary>
/// Issue 10.1 — multi-tenant isolation regression suite. Proves that tenant A's API key can
/// never write data attributed to tenant B, and pins the current (pre-8.6) behavior of the
/// unauthenticated dashboard/timeline read path so the gap is documented rather than hidden.
///
/// Ingestion isolation is structural: the persisted ApplicationId comes from the resolved key
/// (IngestionEndpoints builds the IngestionContext from http.GetResolvedApiKey()), and the
/// ingest DTO has no application_id field — so a spoofed value can't even reach a column.
///
/// Dashboard/timeline reads are NOT yet isolated: those endpoints accept ?app= / {sessionId}
/// unauthenticated. Closing that is Issue 8.6 (RBAC). The read-path tests below assert the
/// current leaky behavior with a loud marker; flip them to 403/empty assertions when 8.6 lands.
/// </summary>
public class MultiTenantIsolationTests : IClassFixture<IngestionWebApplicationFactory>
{
    private readonly IngestionWebApplicationFactory _factory;

    public MultiTenantIsolationTests(IngestionWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.SeedAsync().GetAwaiter().GetResult();
    }

    private HttpClient AuthClient(string key)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthExtensions.HeaderName, key);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    // 1. Tenant A's key + a payload that tries to spoof application_id → the persisted row is
    //    attributed to tenant A (the resolved key), and tenant B gets nothing.
    [Fact]
    public async Task Ingest_WithSpoofedApplicationId_PersistsUnderResolvedKeysTenant()
    {
        var client = AuthClient(_factory.ServerKeyPlaintext); // tenant A
        var distinct = $"isolation-spoof-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/ingest/events", new
        {
            @event = "auth_logout",
            distinct_id = distinct,
            properties = new Dictionary<string, object?>
            {
                ["release_sha"] = "spoof01",
                // Hostile client tries to attribute its event to tenant B.
                ["application_id"] = _factory.SecondAppId.ToString(),
                ["environment_id"] = _factory.SecondEnvId.ToString(),
            },
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();

        var row = await db.Events.AsNoTracking().SingleAsync(e => e.DistinctId == distinct);
        row.ApplicationId.Should().Be(_factory.SeededAppId, "the persisted tenant must be the resolved key's, never the client payload's");
        row.EnvironmentId.Should().Be(_factory.SeededEnvId);

        // Tenant B must have received nothing from tenant A's request.
        (await db.Events.AsNoTracking().AnyAsync(e => e.DistinctId == distinct && e.ApplicationId == _factory.SecondAppId))
            .Should().BeFalse();
    }

    // Sanity / positive control: tenant B's own key writes under tenant B. Confirms the two
    // tenants are genuinely distinct and the spoof test above isn't passing by accident.
    [Fact]
    public async Task Ingest_WithTenantBKey_PersistsUnderTenantB()
    {
        var client = AuthClient(_factory.SecondServerKeyPlaintext); // tenant B
        var distinct = $"isolation-tenantb-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/ingest/events", new
        {
            @event = "auth_logout",
            distinct_id = distinct,
            properties = new Dictionary<string, object?> { ["release_sha"] = "tenantb1" },
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var row = await db.Events.AsNoTracking().SingleAsync(e => e.DistinctId == distinct);
        row.ApplicationId.Should().Be(_factory.SecondAppId);
        row.EnvironmentId.Should().Be(_factory.SecondEnvId);
    }

    // 2. KNOWN GAP — 8.6 RBAC must close. Dashboard reads accept ?app= unauthenticated, so
    //    tenant A's key (ignored by the read path) can read tenant B's events. Asserting the
    //    current leaky behavior; flip to 403/empty when 8.6 lands.
    [Fact]
    public async Task Dashboard_Events_WithCrossTenantAppParam_CurrentlyLeaksTenantBData_KNOWN_GAP_8_6()
    {
        // Seed an event under tenant B via B's own key.
        var distinct = $"isolation-dash-{Guid.NewGuid():N}";
        var tenantB = AuthClient(_factory.SecondServerKeyPlaintext);
        var seed = await tenantB.PostAsJsonAsync("/api/ingest/events", new
        {
            @event = "page_viewed",
            distinct_id = distinct,
            properties = new Dictionary<string, object?> { ["normalized_route"] = "/tenant-b-only", ["feature_area"] = "billing" },
        });
        seed.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Tenant A's key on the dashboard request — but the dashboard ignores the key entirely.
        var tenantA = AuthClient(_factory.ServerKeyPlaintext);
        var res = await tenantA.GetAsync(
            $"/api/dashboard/events?app={_factory.SecondAppId}&env={_factory.SecondEnvId}&distinct_id={distinct}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var total = doc.RootElement.GetProperty("total").GetInt64();

        // KNOWN GAP (Issue 8.6): cross-tenant read is currently possible. When RBAC lands this
        // request must return 403 or an empty result; update this assertion at that point.
        total.Should().BeGreaterThan(0, "PRE-8.6 behavior: dashboard reads are unauthenticated and leak across tenants");
    }

    // 3a. KNOWN GAP — 8.6. Same leak on /api/dashboard/errors.
    [Fact]
    public async Task Dashboard_Errors_WithCrossTenantAppParam_CurrentlyLeaksTenantBData_KNOWN_GAP_8_6()
    {
        var tenantB = AuthClient(_factory.SecondServerKeyPlaintext);
        var seed = await tenantB.PostAsJsonAsync("/api/ingest/errors", new
        {
            error_type = "TenantBError",
            exception_type = $"Tenant.B.Exception{Guid.NewGuid():N}",
            distinct_id = "system:tenant-b",
            properties = new Dictionary<string, object?> { ["endpoint_group"] = "tenant-b-orders", ["http_status_code"] = 500 },
        });
        seed.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var tenantA = AuthClient(_factory.ServerKeyPlaintext);
        var res = await tenantA.GetAsync($"/api/dashboard/errors?app={_factory.SecondAppId}&env={_factory.SecondEnvId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var total = doc.RootElement.GetProperty("total").GetInt64();

        // KNOWN GAP (Issue 8.6): must become 403/empty under RBAC.
        total.Should().BeGreaterThan(0, "PRE-8.6 behavior: dashboard error reads leak across tenants");
    }

    // 3b. KNOWN GAP — 8.6. The session timeline endpoint takes no app param and no auth, so a
    //     session created under tenant B is readable by anyone who knows the session id.
    [Fact]
    public async Task Timeline_ForTenantBSession_CurrentlyReadableWithoutTenantBKey_KNOWN_GAP_8_6()
    {
        var sid = $"isolation-session-{Guid.NewGuid():N}";
        var tenantB = AuthClient(_factory.SecondServerKeyPlaintext);
        var start = await tenantB.PostAsJsonAsync("/api/ingest/sessions/start", new
        {
            session_id = sid,
            distinct_id = "tenant-b-user",
        });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // No tenant B key here — an unauthenticated client reads B's session timeline.
        var anonymous = _factory.CreateClient();
        var res = await anonymous.GetAsync($"/api/sessions/{sid}/timeline");

        // KNOWN GAP (Issue 8.6): the timeline read should be tenant-scoped. Today it is not.
        res.StatusCode.Should().Be(HttpStatusCode.OK, "PRE-8.6 behavior: timeline reads are unauthenticated and not tenant-scoped");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("session").GetProperty("application_id").GetGuid()
            .Should().Be(_factory.SecondAppId);
    }
}
