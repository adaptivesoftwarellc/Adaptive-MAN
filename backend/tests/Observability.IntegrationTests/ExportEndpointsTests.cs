using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Observability.Api.Middleware;
using Observability.Domain.Telemetry;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

public class ExportEndpointsTests : IClassFixture<IngestionWebApplicationFactory>
{
    private readonly IngestionWebApplicationFactory _factory;

    public ExportEndpointsTests(IngestionWebApplicationFactory factory)
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
        return client;
    }

    // Exports now require an explicit `from`; a wide window covers anything seeded "recently".
    private static string WideFrom => $"&from={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-30).ToString("o"))}";

    private static async Task<List<JsonElement>> ParseNdjsonAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        return body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToList();
    }

    private async Task SeedEventsAsync(string eventNamePrefix, int count, DateTime baseTime)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        db.Events.AddRange(Enumerable.Range(0, count).Select(i => new EventRecord
        {
            ApplicationId = _factory.SeededAppId,
            EnvironmentId = _factory.SeededEnvId,
            EventName = $"{eventNamePrefix}_{i}",
            DistinctId = "u_export",
            PropertiesJson = $"{{\"i\":{i}}}",
            OccurredAt = baseTime.AddMinutes(i),
            CreatedAt = baseTime.AddMinutes(i),
        }));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Export_MissingAdminHeader_Returns401()
    {
        var client = AdminClient();
        var resp = await client.GetAsync($"/api/admin/export/events?app={_factory.SeededAppId}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Export_WrongAdminKey_Returns401()
    {
        var client = AdminClient("nope");
        var resp = await client.GetAsync($"/api/admin/export/events?app={_factory.SeededAppId}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Export_MissingApp_Returns400()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var resp = await client.GetAsync("/api/admin/export/events");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("missing_filter");
    }

    [Fact]
    public async Task Export_MissingFrom_Returns400()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var resp = await client.GetAsync($"/api/admin/export/events?app={_factory.SeededAppId}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("missing_filter");
    }

    [Fact]
    public async Task Export_RangeOver90Days_Returns400()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var to = DateTime.UtcNow;
        var from = to.AddDays(-91);
        var resp = await client.GetAsync(
            $"/api/admin/export/events?app={_factory.SeededAppId}" +
            $"&from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("range_too_large");
    }

    [Fact]
    public async Task Export_RangeExactly90Days_IsAllowed()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var to = DateTime.UtcNow;
        var from = to.AddDays(-90);
        var resp = await client.GetAsync(
            $"/api/admin/export/events?app={_factory.SeededAppId}" +
            $"&from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Export_UnsupportedFormat_Returns400()
    {
        var client = AdminClient(_factory.AdminKeyPlaintext);
        var resp = await client.GetAsync($"/api/admin/export/events?app={_factory.SeededAppId}{WideFrom}&format=csv");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("unsupported_format");
    }

    [Fact]
    public async Task Export_Events_StreamsNdjson_RowCountAndContentMatchDb()
    {
        var prefix = $"exp_{Guid.NewGuid():N}".Substring(0, 16);
        var baseTime = DateTime.UtcNow.AddHours(-1);
        await SeedEventsAsync(prefix, 5, baseTime);

        var client = AdminClient(_factory.AdminKeyPlaintext);
        var from = baseTime.AddMinutes(-1);
        var to = baseTime.AddHours(1);
        var resp = await client.GetAsync(
            $"/api/admin/export/events?app={_factory.SeededAppId}&env={_factory.SeededEnvId}" +
            $"&from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/x-ndjson");

        var lines = await ParseNdjsonAsync(resp);
        var mine = lines.Where(l => l.GetProperty("event_name").GetString()!.StartsWith(prefix)).ToList();
        mine.Should().HaveCount(5);

        // Streamed in ascending Id order; raw properties_json preserved verbatim.
        var ids = mine.Select(l => l.GetProperty("id").GetInt64()).ToList();
        ids.Should().BeInAscendingOrder();
        mine[0].GetProperty("properties_json").GetString().Should().Be("{\"i\":0}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var dbCount = await db.Events.CountAsync(e => e.EventName.StartsWith(prefix));
        mine.Should().HaveCount(dbCount);
    }

    [Fact]
    public async Task Export_Events_FilterByEventName_NarrowsResults()
    {
        var prefix = $"flt_{Guid.NewGuid():N}".Substring(0, 16);
        var baseTime = DateTime.UtcNow.AddHours(-1);
        await SeedEventsAsync(prefix, 3, baseTime);

        var client = AdminClient(_factory.AdminKeyPlaintext);
        var resp = await client.GetAsync(
            $"/api/admin/export/events?app={_factory.SeededAppId}{WideFrom}&event_name={prefix}_1");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var lines = await ParseNdjsonAsync(resp);
        lines.Should().OnlyContain(l => l.GetProperty("event_name").GetString() == $"{prefix}_1");
    }

    [Fact]
    public async Task Export_WritesAuditRow_AfterStream()
    {
        var prefix = $"aud_{Guid.NewGuid():N}".Substring(0, 16);
        var baseTime = DateTime.UtcNow.AddHours(-1);
        await SeedEventsAsync(prefix, 2, baseTime);

        var client = AdminClient(_factory.AdminKeyPlaintext);
        var resp = await client.GetAsync(
            $"/api/admin/export/events?app={_factory.SeededAppId}{WideFrom}&event_name={prefix}_0");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = await resp.Content.ReadAsStringAsync(); // drain the stream so the finally-block audit lands

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var audit = await db.AuditLogs
            .Where(a => a.Action == "admin.export.events" && a.ApplicationId == _factory.SeededAppId)
            .OrderByDescending(a => a.OccurredAt)
            .FirstOrDefaultAsync();

        audit.Should().NotBeNull();
        audit!.ActorType.Should().Be("admin_key");
        var details = JsonSerializer.Deserialize<JsonElement>(audit.DetailsJson);
        details.GetProperty("status").GetString().Should().Be("completed");
        details.GetProperty("count").GetInt64().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Export_Errors_StreamsNdjson_RowCountAndContentMatchDb()
    {
        var fpPrefix = $"errfp_{Guid.NewGuid():N}".Substring(0, 16);
        var baseTime = DateTime.UtcNow.AddHours(-1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
            db.Errors.AddRange(Enumerable.Range(0, 4).Select(i => new ErrorRecord
            {
                ApplicationId = _factory.SeededAppId,
                EnvironmentId = _factory.SeededEnvId,
                Fingerprint = $"{fpPrefix}_{i}",
                ErrorType = "server_error",
                OccurrenceCount = i + 1,
                FirstSeenAt = baseTime,
                LastSeenAt = baseTime.AddMinutes(i),
            }));
            await db.SaveChangesAsync();
        }

        var resp = await AdminClient(_factory.AdminKeyPlaintext)
            .GetAsync($"/api/admin/export/errors?app={_factory.SeededAppId}&env={_factory.SeededEnvId}{WideFrom}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/x-ndjson");

        var mine = (await ParseNdjsonAsync(resp))
            .Where(l => l.GetProperty("fingerprint").GetString()!.StartsWith(fpPrefix)).ToList();
        mine.Should().HaveCount(4);
        mine.Select(l => l.GetProperty("last_seen_at").GetDateTime()).Should().BeInAscendingOrder();
        mine[0].GetProperty("error_type").GetString().Should().Be("server_error");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var dbCount = await vdb.Errors.CountAsync(e => e.Fingerprint.StartsWith(fpPrefix));
        mine.Should().HaveCount(dbCount);
    }

    [Fact]
    public async Task Export_SafetyViolations_StreamsNdjson_RowCountAndContentMatchDb()
    {
        var fieldPrefix = $"fld_{Guid.NewGuid():N}".Substring(0, 16);
        var baseTime = DateTime.UtcNow.AddHours(-1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
            db.SafetyViolations.AddRange(Enumerable.Range(0, 3).Select(i => new SafetyViolation
            {
                ApplicationId = _factory.SeededAppId,
                EnvironmentId = _factory.SeededEnvId,
                EventName = "page_viewed",
                RejectedField = $"{fieldPrefix}_{i}",
                Reason = "not_allowlisted",
                CreatedAt = baseTime.AddMinutes(i),
            }));
            await db.SaveChangesAsync();
        }

        var resp = await AdminClient(_factory.AdminKeyPlaintext)
            .GetAsync($"/api/admin/export/safety-violations?app={_factory.SeededAppId}&env={_factory.SeededEnvId}{WideFrom}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/x-ndjson");

        var mine = (await ParseNdjsonAsync(resp))
            .Where(l => l.GetProperty("rejected_field").GetString()!.StartsWith(fieldPrefix)).ToList();
        mine.Should().HaveCount(3);
        mine.Should().OnlyContain(l => l.GetProperty("reason").GetString() == "not_allowlisted");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var dbCount = await vdb.SafetyViolations.CountAsync(v => v.RejectedField.StartsWith(fieldPrefix));
        mine.Should().HaveCount(dbCount);
    }
}
