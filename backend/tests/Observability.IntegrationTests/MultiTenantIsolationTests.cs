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

    // 2. Issue 8.6 (was KNOWN_GAP) — dashboard reads now require auth and are tenant-scoped. An
    //    AppOwner of tenant A requesting tenant B's events is forbidden, not leaked.
    [Fact]
    public async Task Dashboard_Events_CrossTenant_AppOwner_Returns403()
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

        // AppOwner scoped to tenant A asks for tenant B's data → 403 before any query runs.
        var owner = await _factory.BearerClientAsync(_factory.AppOwnerEmail, _factory.AppOwnerPassword);
        var res = await owner.GetAsync(
            $"/api/dashboard/events?app={_factory.SecondAppId}&env={_factory.SecondEnvId}&distinct_id={distinct}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "8.6: AppOwner cannot read other tenants' apps");
    }

    // 3a. Issue 8.6 — same scoping on /api/dashboard/errors.
    [Fact]
    public async Task Dashboard_Errors_CrossTenant_AppOwner_Returns403()
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

        var owner = await _factory.BearerClientAsync(_factory.AppOwnerEmail, _factory.AppOwnerPassword);
        var res = await owner.GetAsync($"/api/dashboard/errors?app={_factory.SecondAppId}&env={_factory.SecondEnvId}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "8.6: AppOwner cannot read other tenants' apps");
    }

    // 3b. Issue 8.6 — the timeline read is now authenticated. An unauthenticated client gets 401, and
    //     an AppOwner of tenant A cannot read a tenant B session (404 — existence is not confirmed).
    [Fact]
    public async Task Timeline_ForTenantBSession_IsTenantScoped()
    {
        var sid = $"isolation-session-{Guid.NewGuid():N}";
        var tenantB = AuthClient(_factory.SecondServerKeyPlaintext);
        var start = await tenantB.PostAsJsonAsync("/api/ingest/sessions/start", new
        {
            session_id = sid,
            distinct_id = "tenant-b-user",
        });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Unauthenticated → 401.
        var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync($"/api/sessions/{sid}/timeline")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "8.6: timeline reads require authentication");

        // AppOwner of tenant A → 404 (not authorized to see this tenant B session).
        var owner = await _factory.BearerClientAsync(_factory.AppOwnerEmail, _factory.AppOwnerPassword);
        (await owner.GetAsync($"/api/sessions/{sid}/timeline")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "8.6: AppOwner cannot read another tenant's session");
    }

    // 4. Issue 8.6 — an AppOwner CAN read its own app (positive control for the scoping above).
    [Fact]
    public async Task Dashboard_Events_OwnApp_AppOwner_Returns200()
    {
        var owner = await _factory.BearerClientAsync(_factory.AppOwnerEmail, _factory.AppOwnerPassword);
        var res = await owner.GetAsync($"/api/dashboard/events?app={_factory.SeededAppId}&env={_factory.SeededEnvId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, "8.6: AppOwner reads its own assigned app");
    }

    // 5. Issue 8.6 — a global-read Admin may read any tenant, and that access is audited.
    [Fact]
    public async Task Dashboard_AdminReadsAnyTenant_AndAccessIsAudited()
    {
        var admin = await _factory.BearerClientAsync(_factory.AdminEmail, _factory.AdminPassword);
        var res = await admin.GetAsync($"/api/dashboard/errors?app={_factory.SecondAppId}&env={_factory.SecondEnvId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, "8.6: Admin is a global reader");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        (await db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == "access.dashboard" && a.ApplicationId == _factory.SecondAppId))
            .Should().BeTrue("8.6 acceptance: Admin/Developer access is logged");
    }
}
