using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Observability.Domain.Telemetry;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

/// <summary>
/// Insights Phase A (docs/product-analytics-plan.md): GET /api/dashboard/insights/trends,
/// GET /api/dashboard/annotations, the admin annotation CRUD, and the health cards_previous
/// deltas. Trends must reject non-catalog event names and untyped breakdowns.
/// </summary>
public class InsightsEndpointsTests : IClassFixture<IngestionWebApplicationFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IngestionWebApplicationFactory _factory;

    public InsightsEndpointsTests(IngestionWebApplicationFactory factory) => _factory = factory;

    private EventRecord NewEvent(string name, DateTime at, string distinctId = "1", string? featureArea = null) => new()
    {
        ApplicationId = _factory.SeededAppId,
        EnvironmentId = _factory.SeededEnvId,
        EventName = name,
        DistinctId = distinctId,
        FeatureArea = featureArea,
        OccurredAt = at,
        CreatedAt = at,
    };

    [Fact]
    public async Task Trends_rejects_non_catalog_event_names()
    {
        await _factory.SeedAsync();
        var client = await _factory.BearerClientAsync(_factory.AdminEmail, _factory.AdminPassword);

        var res = await client.GetAsync(
            $"/api/dashboard/insights/trends?app={_factory.SeededAppId}&env={_factory.SeededEnvId}&events=totally_made_up");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("unknown_event", body);
    }

    [Fact]
    public async Task Trends_rejects_untyped_breakdown_and_bad_agg()
    {
        await _factory.SeedAsync();
        var client = await _factory.BearerClientAsync(_factory.AdminEmail, _factory.AdminPassword);
        var baseUrl = $"/api/dashboard/insights/trends?app={_factory.SeededAppId}&env={_factory.SeededEnvId}&events=page_viewed";

        var badBreakdown = await client.GetAsync($"{baseUrl}&breakdown=properties_json");
        Assert.Equal(HttpStatusCode.BadRequest, badBreakdown.StatusCode);

        var badAgg = await client.GetAsync($"{baseUrl}&agg=sum");
        Assert.Equal(HttpStatusCode.BadRequest, badAgg.StatusCode);

        // unique_users cannot roll up to weeks — explicit 400, not silently-wrong numbers.
        var badCombo = await client.GetAsync($"{baseUrl}&agg=unique_users&interval=week");
        Assert.Equal(HttpStatusCode.BadRequest, badCombo.StatusCode);
    }

    [Fact]
    public async Task Trends_counts_and_breaks_down_by_feature_area()
    {
        await _factory.SeedAsync();
        var now = DateTime.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
            db.Events.AddRange(
                NewEvent("page_viewed", now.AddMinutes(-10), "1", "ivr"),
                NewEvent("page_viewed", now.AddMinutes(-20), "2", "ivr"),
                NewEvent("page_viewed", now.AddMinutes(-30), "1", "worklist"),
                NewEvent("auth_login_success", now.AddMinutes(-40), "1"));
            await db.SaveChangesAsync();
        }

        var client = await _factory.BearerClientAsync(_factory.AdminEmail, _factory.AdminPassword);
        var res = await client.GetFromJsonAsync<TrendsResponse>(
            $"/api/dashboard/insights/trends?app={_factory.SeededAppId}&env={_factory.SeededEnvId}" +
            "&events=page_viewed,auth_login_success&breakdown=feature_area", Json);

        Assert.NotNull(res);
        // page_viewed splits into ivr(2) + worklist(1); auth_login_success has no feature_area -> "(none)".
        var ivr = res!.Series.Single(s => s.Event == "page_viewed" && s.Breakdown == "ivr");
        Assert.Equal(2, ivr.Total);
        var worklist = res.Series.Single(s => s.Event == "page_viewed" && s.Breakdown == "worklist");
        Assert.Equal(1, worklist.Total);
        var login = res.Series.Single(s => s.Event == "auth_login_success");
        Assert.Equal("(none)", login.Breakdown);
        Assert.Equal(1, login.Total);
    }

    [Fact]
    public async Task Trends_unique_users_counts_distinct_ids_not_rows()
    {
        await _factory.SeedAsync();
        var now = DateTime.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
            // Three rows, two distinct users, same hour bucket.
            db.Events.AddRange(
                NewEvent("page_viewed", now.AddMinutes(-5), "42"),
                NewEvent("page_viewed", now.AddMinutes(-6), "42"),
                NewEvent("page_viewed", now.AddMinutes(-7), "43"));
            await db.SaveChangesAsync();
        }

        var client = await _factory.BearerClientAsync(_factory.AdminEmail, _factory.AdminPassword);
        var res = await client.GetFromJsonAsync<TrendsResponse>(
            $"/api/dashboard/insights/trends?app={_factory.SeededAppId}&env={_factory.SeededEnvId}" +
            "&events=page_viewed&agg=unique_users&interval=day", Json);

        Assert.NotNull(res);
        var series = res!.Series.Single(s => s.Event == "page_viewed");
        Assert.Equal(2, series.Total);
    }

    [Fact]
    public async Task Annotations_admin_crud_and_dashboard_read()
    {
        await _factory.SeedAsync();
        var admin = await _factory.BearerClientAsync(_factory.AdminEmail, _factory.AdminPassword);

        var create = await admin.PostAsJsonAsync("/api/admin/annotations", new
        {
            application_id = _factory.SeededAppId,
            environment_id = _factory.SeededEnvId,
            label = "deploy 1.2.3",
            release_sha = "abc1234",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = JsonSerializer.Deserialize<AnnotationRow>(await create.Content.ReadAsStringAsync(), Json)!;
        Assert.Equal("deploy 1.2.3", created.Label);

        // Dashboard read sees it inside the default 24h window.
        var read = await admin.GetFromJsonAsync<AnnotationsResponse>(
            $"/api/dashboard/annotations?app={_factory.SeededAppId}&env={_factory.SeededEnvId}", Json);
        Assert.NotNull(read);
        Assert.Contains(read!.Rows, r => r.Label == "deploy 1.2.3" && r.ReleaseSha == "abc1234");

        // Mismatched app/env pair is rejected.
        var badPair = await admin.PostAsJsonAsync("/api/admin/annotations", new
        {
            application_id = _factory.SeededAppId,
            environment_id = Guid.NewGuid(),
            label = "nope",
        });
        Assert.Equal(HttpStatusCode.NotFound, badPair.StatusCode);

        var delete = await admin.DeleteAsync($"/api/admin/annotations/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var readAfter = await admin.GetFromJsonAsync<AnnotationsResponse>(
            $"/api/dashboard/annotations?app={_factory.SeededAppId}&env={_factory.SeededEnvId}", Json);
        Assert.DoesNotContain(readAfter!.Rows, r => r.Id == created.Id);
    }

    [Fact]
    public async Task Health_returns_previous_window_cards_for_deltas()
    {
        await _factory.SeedAsync();
        // Fresh app/env ids: the class fixture shares one InMemory store, so counting against the
        // seeded app would race the other tests' page_viewed rows.
        var appId = Guid.NewGuid();
        var envId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
            // Two page views inside the current 24h window, one in the previous window.
            db.Events.AddRange(
                new EventRecord { ApplicationId = appId, EnvironmentId = envId, EventName = "page_viewed", DistinctId = "1", OccurredAt = now.AddHours(-1), CreatedAt = now.AddHours(-1) },
                new EventRecord { ApplicationId = appId, EnvironmentId = envId, EventName = "page_viewed", DistinctId = "2", OccurredAt = now.AddHours(-2), CreatedAt = now.AddHours(-2) },
                new EventRecord { ApplicationId = appId, EnvironmentId = envId, EventName = "page_viewed", DistinctId = "1", OccurredAt = now.AddHours(-30), CreatedAt = now.AddHours(-30) });
            await db.SaveChangesAsync();
        }

        var client = await _factory.BearerClientAsync(_factory.AdminEmail, _factory.AdminPassword);
        var res = await client.GetFromJsonAsync<HealthResponse>(
            $"/api/dashboard/health?app={appId}&env={envId}", Json);

        Assert.NotNull(res);
        Assert.Equal(2, res!.Cards.PageViews);
        Assert.NotNull(res.CardsPrevious);
        Assert.Equal(1, res.CardsPrevious!.PageViews);
    }

    private sealed record TrendsResponse(
        [property: JsonPropertyName("series")] List<TrendSeriesRow> Series);

    private sealed record TrendSeriesRow(
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("breakdown")] string? Breakdown,
        [property: JsonPropertyName("total")] long Total);

    private sealed record AnnotationsResponse(
        [property: JsonPropertyName("rows")] List<AnnotationRow> Rows);

    private sealed record AnnotationRow(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("release_sha")] string? ReleaseSha);

    private sealed record HealthResponse(
        [property: JsonPropertyName("cards")] HealthCards Cards,
        [property: JsonPropertyName("cards_previous")] HealthCards? CardsPrevious);

    private sealed record HealthCards(
        [property: JsonPropertyName("page_views")] long PageViews);
}
