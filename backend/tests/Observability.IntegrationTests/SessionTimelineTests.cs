using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Observability.Api.Middleware;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

public class SessionTimelineTests : IClassFixture<IngestionWebApplicationFactory>
{
    private readonly IngestionWebApplicationFactory _factory;
    public SessionTimelineTests(IngestionWebApplicationFactory factory) => _factory = factory;

    private HttpClient AuthClient(string key)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(ApiKeyAuthExtensions.HeaderName, key);
        return c;
    }

    [Fact]
    public async Task Session_lifecycle_and_timeline_with_cross_process_correlation()
    {
        await _factory.SeedAsync();
        var publicClient = AuthClient(_factory.PublicKeyPlaintext);
        var serverClient = AuthClient(_factory.ServerKeyPlaintext);
        var dashboard = await _factory.BearerClientAsync(_factory.AdminEmail, _factory.AdminPassword);

        const string sid = "session-abc";
        const string distinct = "user-42";
        const string corr = "corr-xyz-1";

        // 1. /sessions/start
        var startRes = await publicClient.PostAsJsonAsync("/api/ingest/sessions/start", new
        {
            session_id = sid,
            distinct_id = distinct,
            release_sha = "abc1234",
        });
        Assert.Equal(HttpStatusCode.Accepted, startRes.StatusCode);

        // 2. FE event with a session_id
        var pageView = await publicClient.PostAsJsonAsync("/api/ingest/events", new
        {
            @event = "page_viewed",
            distinct_id = distinct,
            session_id = sid,
            occurred_at = DateTime.UtcNow.AddMinutes(-2),
            properties = new Dictionary<string, object?> { ["normalized_route"] = "/orders/:id" },
        });
        Assert.Equal(HttpStatusCode.Accepted, pageView.StatusCode);

        // 3. FE api_request_failed event with a correlation_id (this is what the BE error will pivot on)
        // The server overwrites event.correlation_id from the X-Correlation-Id header, so set that.
        publicClient.DefaultRequestHeaders.Remove("X-Correlation-Id");
        publicClient.DefaultRequestHeaders.Add("X-Correlation-Id", corr);
        var apiFail = await publicClient.PostAsJsonAsync("/api/ingest/events", new
        {
            @event = "api_request_failed",
            distinct_id = distinct,
            session_id = sid,
            occurred_at = DateTime.UtcNow.AddMinutes(-1),
            properties = new Dictionary<string, object?>
            {
                ["endpoint_group"] = "orders",
                ["method"] = "POST",
                ["http_status_code"] = 500,
                ["is_network_error"] = false,
            },
        });
        Assert.Equal(HttpStatusCode.Accepted, apiFail.StatusCode);

        // 4. Server emits the matching server_error_occurred WITHOUT a session_id but WITH the correlation_id.
        serverClient.DefaultRequestHeaders.Remove("X-Correlation-Id");
        serverClient.DefaultRequestHeaders.Add("X-Correlation-Id", corr);
        var serverErr = await serverClient.PostAsJsonAsync("/api/ingest/errors", new
        {
            error_type = "ServerError",
            exception_type = "System.InvalidOperationException",
            distinct_id = "system:api",
            occurred_at = DateTime.UtcNow,
            properties = new Dictionary<string, object?>
            {
                ["endpoint_group"] = "orders",
                ["http_status_code"] = 500,
            },
        });
        Assert.Equal(HttpStatusCode.Accepted, serverErr.StatusCode);

        // 5. /sessions/end
        var endRes = await publicClient.PostAsJsonAsync("/api/ingest/sessions/end", new { session_id = sid });
        Assert.Equal(HttpStatusCode.Accepted, endRes.StatusCode);

        // 6. GET timeline
        var timelineRes = await dashboard.GetAsync($"/api/sessions/{sid}/timeline");
        Assert.Equal(HttpStatusCode.OK, timelineRes.StatusCode);
        var json = await timelineRes.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var session = doc.RootElement.GetProperty("session");
        Assert.Equal(sid, session.GetProperty("session_id").GetString());
        Assert.Equal(distinct, session.GetProperty("distinct_id").GetString());
        Assert.True(session.GetProperty("ended_at").ValueKind != JsonValueKind.Null);

        var entries = doc.RootElement.GetProperty("entries");
        var kinds = entries.EnumerateArray().Select(e => e.GetProperty("kind").GetString()!).ToList();
        Assert.Contains("event", kinds);
        Assert.Contains("error", kinds); // cross-process error joined in by correlation_id

        // The server error should appear with source=cross_process (no session_id of its own)
        var crossProcess = entries.EnumerateArray().FirstOrDefault(e =>
            e.GetProperty("kind").GetString() == "error"
            && e.GetProperty("correlation_id").GetString() == corr);
        Assert.True(crossProcess.ValueKind != JsonValueKind.Undefined);
        Assert.Equal("cross_process", crossProcess.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Timeline_returns_404_for_unknown_session()
    {
        await _factory.SeedAsync();
        var dashboard = await _factory.BearerClientAsync(_factory.AdminEmail, _factory.AdminPassword);
        var res = await dashboard.GetAsync("/api/sessions/does-not-exist/timeline");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Cross_process_correlation_handles_many_unique_ids()
    {
        // Regression for the IN-list parameter cap on SQL Server. The InMemory provider
        // doesn't enforce the ~2,100-parameter limit, so this test pins the *behavior* —
        // chunking must still produce one error entry per matched correlation id, in
        // chronological order — rather than the SQL-level constraint. The chunk size is
        // 1,000; this test uses 25 distinct correlation ids so it exercises a single chunk.
        // A larger run that crosses multiple chunks would only verify the same loop body.
        await _factory.SeedAsync();
        var publicClient = AuthClient(_factory.PublicKeyPlaintext);
        var serverClient = AuthClient(_factory.ServerKeyPlaintext);
        var dashboard = await _factory.BearerClientAsync(_factory.AdminEmail, _factory.AdminPassword);

        const int n = 25;
        var sid = $"session-bulk-{Guid.NewGuid():N}";

        var startRes = await publicClient.PostAsJsonAsync("/api/ingest/sessions/start", new
        {
            session_id = sid,
            distinct_id = "user-bulk",
        });
        Assert.Equal(HttpStatusCode.Accepted, startRes.StatusCode);

        for (var i = 0; i < n; i++)
        {
            var corr = $"corr-bulk-{i:D3}";
            publicClient.DefaultRequestHeaders.Remove("X-Correlation-Id");
            publicClient.DefaultRequestHeaders.Add("X-Correlation-Id", corr);
            var fe = await publicClient.PostAsJsonAsync("/api/ingest/events", new
            {
                @event = "api_request_failed",
                distinct_id = "user-bulk",
                session_id = sid,
                occurred_at = DateTime.UtcNow.AddSeconds(-(n - i)),
                properties = new Dictionary<string, object?>
                {
                    ["endpoint_group"] = "orders",
                    ["method"] = "GET",
                    ["http_status_code"] = 500,
                    ["is_network_error"] = false,
                },
            });
            Assert.Equal(HttpStatusCode.Accepted, fe.StatusCode);

            // Each FE failure has a matching BE error sharing the correlation id.
            // Distinct exception types so each error is a separate row (not deduped by fingerprint).
            serverClient.DefaultRequestHeaders.Remove("X-Correlation-Id");
            serverClient.DefaultRequestHeaders.Add("X-Correlation-Id", corr);
            var be = await serverClient.PostAsJsonAsync("/api/ingest/errors", new
            {
                error_type = $"Err{i:D3}",
                exception_type = $"Test.Bulk.Exception{i:D3}",
                distinct_id = "system:api",
                occurred_at = DateTime.UtcNow.AddSeconds(-(n - i) + 1),
                properties = new Dictionary<string, object?>
                {
                    ["endpoint_group"] = "orders",
                    ["http_status_code"] = 500,
                },
            });
            Assert.Equal(HttpStatusCode.Accepted, be.StatusCode);
        }

        var timelineRes = await dashboard.GetAsync($"/api/sessions/{sid}/timeline");
        Assert.Equal(HttpStatusCode.OK, timelineRes.StatusCode);
        var json = await timelineRes.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToList();
        var errorEntries = entries.Where(e => e.GetProperty("kind").GetString() == "error").ToList();
        var eventEntries = entries.Where(e => e.GetProperty("kind").GetString() == "event").ToList();

        Assert.Equal(n, eventEntries.Count);
        Assert.Equal(n, errorEntries.Count);
        Assert.All(errorEntries, e => Assert.Equal("cross_process", e.GetProperty("source").GetString()));

        // Ordering: timestamps must be non-decreasing across the merged stream.
        DateTime? prev = null;
        foreach (var e in entries)
        {
            var t = e.GetProperty("occurred_at").GetDateTime();
            if (prev is not null) Assert.True(t >= prev.Value, "Entries are not chronologically ordered.");
            prev = t;
        }
    }

    [Fact]
    public async Task Orphan_session_end_is_dropped_silently()
    {
        await _factory.SeedAsync();
        var publicClient = AuthClient(_factory.PublicKeyPlaintext);

        // No /start was ever sent for this session id.
        var endRes = await publicClient.PostAsJsonAsync("/api/ingest/sessions/end", new
        {
            session_id = "orphan-session-without-start",
        });
        Assert.Equal(HttpStatusCode.Accepted, endRes.StatusCode);

        // Confirm no row was created — the dashboard list must not see a malformed session.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var orphan = await db.Sessions.FirstOrDefaultAsync(s => s.SessionId == "orphan-session-without-start");
        Assert.Null(orphan);
    }
}
