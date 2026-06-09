using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Observability.Api.Middleware;
using Observability.Domain.Applications;
using Observability.Domain.Audit;
using Observability.Infrastructure.Authentication;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

public class AdminEndpointsTests : IClassFixture<IngestionWebApplicationFactory>
{
    private readonly IngestionWebApplicationFactory _factory;

    public AdminEndpointsTests(IngestionWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.SeedAsync().GetAwaiter().GetResult();
    }

    private HttpClient AdminClient(string? key = null)
    {
        var client = _factory.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add(AdminKeyAuthExtensions.HeaderName, key);
        }
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    [Fact]
    public async Task CreateApp_MissingAdminHeader_Returns401()
    {
        var client = AdminClient();
        var response = await client.PostAsJsonAsync("/api/admin/apps",
            new { name = "X", slug = "x", environments = new[] { "Development" } });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateApp_WrongAdminKey_Returns401()
    {
        var client = AdminClient("nope");
        var response = await client.PostAsJsonAsync("/api/admin/apps",
            new { name = "X", slug = "x", environments = new[] { "Development" } });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateApp_NewSlug_Returns201_AndIsIdempotent()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var slug = $"admin-test-{Guid.NewGuid():N}".Substring(0, 16);

        var first = await client.PostAsJsonAsync("/api/admin/apps",
            new { name = "Admin Test", slug, environments = new[] { "Development", "UAT" } });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        firstBody.GetProperty("created").GetBoolean().Should().BeTrue();
        firstBody.GetProperty("environments").GetArrayLength().Should().Be(2);

        // Idempotent: same slug returns 200 + created=false, no duplicate row.
        var second = await client.PostAsJsonAsync("/api/admin/apps",
            new { name = "Admin Test", slug, environments = new[] { "Development", "UAT" } });
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        secondBody.GetProperty("created").GetBoolean().Should().BeFalse();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        (await db.Applications.CountAsync(a => a.Slug == slug)).Should().Be(1);
        (await db.AuditLogs.CountAsync(a => a.Action == "admin.app.created")).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task MintKey_Server_ReturnsPlaintextOnce_AndResolverAccepts()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var slug = $"key-test-{Guid.NewGuid():N}".Substring(0, 16);

        var createResp = await client.PostAsJsonAsync("/api/admin/apps",
            new { name = "Key Test", slug, environments = new[] { "Development" } });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var mintResp = await client.PostAsJsonAsync(
            $"/api/admin/apps/{slug}/environments/Development/keys",
            new { key_type = "server_api" });
        mintResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await mintResp.Content.ReadFromJsonAsync<JsonElement>();
        var plaintext = body.GetProperty("plaintext_key").GetString()!;
        plaintext.Should().StartWith("aoserv_");
        body.GetProperty("key_type").GetString().Should().Be("ServerApi");

        // Resolver accepts the minted key (hash matches what ApiKeyResolver would compute).
        using var scope = _factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IApiKeyResolver>();
        var resolved = await resolver.ResolveAsync(plaintext, CancellationToken.None);
        resolved.Should().NotBeNull();
        resolved!.KeyType.Should().Be(ApiKeyType.ServerApi);

        // Only the hash is persisted — never the plaintext.
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var keyRow = await db.ApiKeys.SingleAsync(k => k.Id == body.GetProperty("id").GetGuid());
        keyRow.KeyHash.Should().NotBe(plaintext);
        keyRow.KeyHash.Should().NotContain(plaintext);
    }

    [Fact]
    public async Task MintKey_PublicClient_ReturnsAopubPrefix()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var resp = await client.PostAsJsonAsync(
            $"/api/admin/apps/test-app/environments/Development/keys",
            new { key_type = "public_client" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("plaintext_key").GetString().Should().StartWith("aopub_");
    }

    [Fact]
    public async Task MintKey_UnknownApp_Returns404()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var resp = await client.PostAsJsonAsync(
            "/api/admin/apps/does-not-exist/environments/Development/keys",
            new { key_type = "server_api" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MintKey_InvalidKeyType_Returns400()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var resp = await client.PostAsJsonAsync(
            "/api/admin/apps/test-app/environments/Development/keys",
            new { key_type = "garbage" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Issue 8.7 read-only audit view: GET /api/admin/audit ----

    private async Task SeedAuditAsync(params AuditLog[] rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        db.AuditLogs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static AuditLog AuditRow(string action, DateTime occurredAt, Guid? appId = null) => new()
    {
        Action = action,
        ActorType = "admin_key",
        ApplicationId = appId,
        OccurredAt = occurredAt,
        DetailsJson = "{}",
    };

    // ---- Issue 10.6 admin UI: list apps, list keys, revoke ----

    private HttpClient IngestClient(string key)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthExtensions.HeaderName, key);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    [Fact]
    public async Task ListApps_MissingAdminHeader_Returns401()
    {
        var resp = await AdminClient().GetAsync("/api/admin/apps");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>Creates a fresh app+env and mints a known set of keys so count assertions are isolated
    /// from the other tests that mutate the shared <c>test-app</c> fixture. Returns the slug.</summary>
    private async Task<string> SeedAppWithKeysAsync(HttpClient admin, int total, int revoked)
    {
        var slug = $"keys-{Guid.NewGuid():N}".Substring(0, 14);
        await admin.PostAsJsonAsync("/api/admin/apps", new { name = "Keys Test", slug, environments = new[] { "Development" } });

        for (var i = 0; i < total; i++)
        {
            var mint = await (await admin.PostAsJsonAsync(
                $"/api/admin/apps/{slug}/environments/Development/keys", new { key_type = "server_api" }))
                .Content.ReadFromJsonAsync<JsonElement>();
            if (i < revoked)
            {
                var id = mint.GetProperty("id").GetGuid();
                await admin.PostAsync($"/api/admin/apps/{slug}/environments/Development/keys/{id}/revoke", null);
            }
        }
        return slug;
    }

    [Fact]
    public async Task ListApps_ReturnsAppsWithEnvironmentsAndKeyCounts()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var slug = await SeedAppWithKeysAsync(client, total: 3, revoked: 1);

        var body = await (await client.GetAsync("/api/admin/apps")).Content.ReadFromJsonAsync<JsonElement>();
        var app = body.GetProperty("apps").EnumerateArray()
            .Single(a => a.GetProperty("slug").GetString() == slug);
        var dev = app.GetProperty("environments").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "Development");

        dev.GetProperty("total_key_count").GetInt32().Should().Be(3);
        dev.GetProperty("active_key_count").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ListKeys_ReturnsKeys_WithMaskingFieldsAndRevokedState()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var slug = await SeedAppWithKeysAsync(client, total: 3, revoked: 1);

        var body = await (await client.GetAsync($"/api/admin/apps/{slug}/environments/Development/keys"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var keys = body.GetProperty("keys").EnumerateArray().ToList();
        keys.Should().HaveCount(3);
        keys.Count(k => k.GetProperty("is_active").GetBoolean()).Should().Be(2);
        // Read-only projection exposes the display fields for every key (last_used_at is null/omitted
        // until the key actually authenticates a request)...
        foreach (var k in keys)
        {
            k.TryGetProperty("created_at", out _).Should().BeTrue();
            k.TryGetProperty("key_type", out _).Should().BeTrue();
            k.TryGetProperty("is_active", out _).Should().BeTrue();
        }
        // ...and never the hash or plaintext.
        body.ToString().Should().NotContain("KeyHash").And.NotContain("plaintext");
    }

    [Fact]
    public async Task ListKeys_UnknownApp_Returns404()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var resp = await client.GetAsync($"/api/admin/apps/no-such-{Guid.NewGuid():N}/environments/Development/keys");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevokeKey_MintUseRevoke_ThenIngestReturns401_AndAudits()
    {
        var admin = AdminClient(_factory.AdminKeyPlaintext);

        // Mint a fresh server key on the seeded app/env.
        var mint = await (await admin.PostAsJsonAsync(
            "/api/admin/apps/test-app/environments/Development/keys",
            new { key_type = "server_api" })).Content.ReadFromJsonAsync<JsonElement>();
        var keyId = mint.GetProperty("id").GetGuid();
        var plaintext = mint.GetProperty("plaintext_key").GetString()!;

        // The minted key authenticates ingest.
        var ingest = IngestClient(plaintext);
        var before = await ingest.PostAsJsonAsync("/api/ingest/events", new { @event = "auth_logout", distinct_id = "42" });
        before.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Revoke it.
        var revoke = await admin.PostAsync(
            $"/api/admin/apps/test-app/environments/Development/keys/{keyId}/revoke", content: null);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);
        (await revoke.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("already_revoked").GetBoolean().Should().BeFalse();

        // Same key is now rejected.
        var after = await IngestClient(plaintext)
            .PostAsJsonAsync("/api/ingest/events", new { @event = "auth_logout", distinct_id = "42" });
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // An admin.key.revoked audit row was written for this key.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        (await db.AuditLogs.CountAsync(a => a.Action == "admin.key.revoked"
            && a.DetailsJson.Contains(keyId.ToString()))).Should().BeGreaterThanOrEqualTo(1);
        (await db.ApiKeys.SingleAsync(k => k.Id == keyId)).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RevokeKey_AlreadyRevoked_IsIdempotent()
    {
        var admin = AdminClient(_factory.AdminKeyPlaintext);
        var mint = await (await admin.PostAsJsonAsync(
            "/api/admin/apps/test-app/environments/Development/keys",
            new { key_type = "public_client" })).Content.ReadFromJsonAsync<JsonElement>();
        var keyId = mint.GetProperty("id").GetGuid();

        var first = await admin.PostAsync($"/api/admin/apps/test-app/environments/Development/keys/{keyId}/revoke", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await admin.PostAsync($"/api/admin/apps/test-app/environments/Development/keys/{keyId}/revoke", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("already_revoked").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RevokeKey_UnknownKey_Returns404()
    {
        var admin = AdminClient(_factory.AdminKeyPlaintext);
        var resp = await admin.PostAsync(
            $"/api/admin/apps/test-app/environments/Development/keys/{Guid.NewGuid()}/revoke", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAudit_MissingAdminHeader_Returns401()
    {
        var client = AdminClient();
        var resp = await client.GetAsync("/api/admin/audit");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAudit_WrongAdminKey_Returns401()
    {
        var client = AdminClient("nope");
        var resp = await client.GetAsync("/api/admin/audit");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAudit_NoMatches_ReturnsEmptyEnvelope()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var action = $"admin.nonexistent.{Guid.NewGuid():N}";

        var resp = await client.GetAsync($"/api/admin/audit?action={action}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("total").GetInt64().Should().Be(0);
        body.GetProperty("rows").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetAudit_Pagination_PagesAreDisjointAndTotalStable()
    {
        var action = $"admin.test.page.{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        // 5 rows, distinct timestamps so DESC order is deterministic.
        await SeedAuditAsync(Enumerable.Range(0, 5)
            .Select(i => AuditRow(action, now.AddMinutes(-i)))
            .ToArray());

        var client = AdminClient(_factory.AdminKeyPlaintext);

        var p0 = await (await client.GetAsync($"/api/admin/audit?action={action}&page=0&page_size=2"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var p1 = await (await client.GetAsync($"/api/admin/audit?action={action}&page=1&page_size=2"))
            .Content.ReadFromJsonAsync<JsonElement>();

        p0.GetProperty("total").GetInt64().Should().Be(5);
        p1.GetProperty("total").GetInt64().Should().Be(5);
        p0.GetProperty("page").GetInt32().Should().Be(0);
        p1.GetProperty("page").GetInt32().Should().Be(1);
        p0.GetProperty("rows").GetArrayLength().Should().Be(2);
        p1.GetProperty("rows").GetArrayLength().Should().Be(2);

        var ids0 = p0.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetGuid());
        var ids1 = p1.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetGuid());
        ids0.Intersect(ids1).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAudit_FilterByAction_ReturnsOnlyMatching()
    {
        var mine = $"admin.test.action.{Guid.NewGuid():N}";
        var other = $"admin.test.other.{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        await SeedAuditAsync(
            AuditRow(mine, now),
            AuditRow(mine, now.AddMinutes(-1)),
            AuditRow(other, now));

        var client = AdminClient(_factory.AdminKeyPlaintext);
        var body = await (await client.GetAsync($"/api/admin/audit?action={mine}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("total").GetInt64().Should().Be(2);
        body.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("action").GetString())
            .Should().OnlyContain(a => a == mine);
    }

    [Fact]
    public async Task GetAudit_FilterByDateRange_ReturnsInRangeRowsDescending()
    {
        var action = $"admin.test.range.{Guid.NewGuid():N}";
        var baseTime = DateTime.UtcNow.AddHours(-10);
        await SeedAuditAsync(
            AuditRow(action, baseTime.AddHours(-5)), // before window
            AuditRow(action, baseTime.AddHours(1)),  // in window
            AuditRow(action, baseTime.AddHours(2)),  // in window
            AuditRow(action, baseTime.AddHours(10))); // after window

        var from = Uri.EscapeDataString(baseTime.ToString("o"));
        var to = Uri.EscapeDataString(baseTime.AddHours(3).ToString("o"));

        var client = AdminClient(_factory.AdminKeyPlaintext);
        var body = await (await client.GetAsync($"/api/admin/audit?action={action}&from={from}&to={to}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("total").GetInt64().Should().Be(2);
        var times = body.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("occurred_at").GetDateTime())
            .ToList();
        times.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task GetAudit_FilterByApp_SlugAndIdResolveToSameRows()
    {
        var action = $"admin.test.app.{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        await SeedAuditAsync(
            AuditRow(action, now, _factory.SeededAppId),
            AuditRow(action, now.AddMinutes(-1), _factory.SeededAppId),
            AuditRow(action, now, _factory.SecondAppId));

        var client = AdminClient(_factory.AdminKeyPlaintext);

        var bySlug = await (await client.GetAsync($"/api/admin/audit?action={action}&app=test-app"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var byId = await (await client.GetAsync($"/api/admin/audit?action={action}&app={_factory.SeededAppId}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        bySlug.GetProperty("total").GetInt64().Should().Be(2);
        byId.GetProperty("total").GetInt64().Should().Be(2);
        bySlug.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("application_id").GetGuid())
            .Should().OnlyContain(id => id == _factory.SeededAppId);
    }

    [Fact]
    public async Task GetAudit_UnknownAppSlug_ReturnsEmptyNotError()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var resp = await client.GetAsync($"/api/admin/audit?app=no-such-app-{Guid.NewGuid():N}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("total").GetInt64().Should().Be(0);
        body.GetProperty("rows").GetArrayLength().Should().Be(0);
    }
}
