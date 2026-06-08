using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Observability.Api.Middleware;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

/// <summary>
/// Issue 10.4 — the /api/v1 mirror is byte-for-byte equivalent to the unprefixed routes, and the
/// SDK version header is read (log-only) on the ingest surface.
/// </summary>
public class ApiVersioningTests : IClassFixture<IngestionWebApplicationFactory>
{
    private readonly IngestionWebApplicationFactory _factory;

    public ApiVersioningTests(IngestionWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.SeedAsync().GetAwaiter().GetResult();
    }

    private HttpClient AuthClient(string key, HttpClient? client = null)
    {
        client ??= _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthExtensions.HeaderName, key);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    [Theory]
    [InlineData("/api/ingest/events")]
    [InlineData("/api/v1/ingest/events")]
    public async Task Events_BothPaths_Accepted_AndPersistIdenticalRows(string path)
    {
        var client = AuthClient(_factory.PublicKeyPlaintext);
        var route = $"/versioning{path.Replace("/", "-")}";
        var response = await client.PostAsJsonAsync(path, new
        {
            @event = "page_viewed",
            distinct_id = "v-test",
            properties = new { normalized_route = route, feature_area = "versioning" }
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var rows = await db.Events.Where(e => e.NormalizedRoute == route).ToListAsync();
        rows.Should().ContainSingle();
        rows[0].EventName.Should().Be("page_viewed");
        rows[0].FeatureArea.Should().Be("versioning");
        rows[0].CorrelationId.Should().NotBeNull();
    }

    [Fact]
    public async Task V1_MissingAuth_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/ingest/events", new { @event = "auth_logout", distinct_id = "42" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task V1_SessionStart_ThenTimeline_RoundTrips()
    {
        var client = AuthClient(_factory.ServerKeyPlaintext);
        var sessionId = $"v1-session-{Guid.NewGuid():N}";

        var start = await client.PostAsJsonAsync("/api/v1/ingest/sessions/start", new
        {
            session_id = sessionId,
            distinct_id = "v-test",
        });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var timeline = await client.GetAsync($"/api/v1/sessions/{sessionId}/timeline");
        timeline.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task V1_OversizePayload_Returns413()
    {
        var client = AuthClient(_factory.ServerKeyPlaintext);
        // Exceed the 64 KB ingest cap — proves the payload middleware is scoped to /api/v1 too.
        var big = new string('x', 70 * 1024);
        var response = await client.PostAsJsonAsync("/api/v1/ingest/events", new
        {
            @event = "page_viewed",
            distinct_id = "v-test",
            properties = new { normalized_route = "/x", feature_area = big }
        });
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task MissingSdkVersionHeader_LogsInformation()
    {
        var sink = new CapturingLoggerProvider();
        // No auth needed: the SDK-version middleware runs before the api-key endpoint filter.
        var client = _factory.WithWebHostBuilder(b => b.ConfigureLogging(lb => lb.AddProvider(sink))).CreateClient();

        await client.PostAsJsonAsync("/api/ingest/events", new { @event = "auth_logout", distinct_id = "42" });

        // Information, not Warning — a header-less deployed fleet must not flood logs at Warning.
        sink.Messages.Should().Contain(m => m.Contains("missing") && m.StartsWith("Information"));
    }

    // Floor behavior is read at app-build time from config, so it's exercised by invoking the
    // middleware directly with a fixed floor — deterministic, no host-config timing involved.
    [Fact]
    public async Task Middleware_SuppliedVersionBelowFloor_LogsWarning_CapturingVersion()
    {
        var logger = new ListLogger<SdkVersionMiddleware>();
        var mw = new SdkVersionMiddleware(_ => Task.CompletedTask, logger, "1.0.0");
        var ctx = new DefaultHttpContext { Request = { Path = "/api/ingest/events" } };
        ctx.Request.Headers[SdkVersionMiddleware.HeaderName] = "js/0.2.0";

        await mw.InvokeAsync(ctx);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("0.2.0") && e.Message.Contains("below"));
    }

    [Fact]
    public async Task Middleware_SuppliedVersionAtOrAboveFloor_DoesNotWarn()
    {
        var logger = new ListLogger<SdkVersionMiddleware>();
        var mw = new SdkVersionMiddleware(_ => Task.CompletedTask, logger, "1.0.0");
        var ctx = new DefaultHttpContext { Request = { Path = "/api/ingest/events" } };
        ctx.Request.Headers[SdkVersionMiddleware.HeaderName] = "dotnet/1.4.0";

        await mw.InvokeAsync(ctx);

        logger.Entries.Should().BeEmpty();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Messages);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly ConcurrentQueue<string> _sink;
            public CapturingLogger(string category, ConcurrentQueue<string> sink) { _category = category; _sink = sink; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (_category == typeof(SdkVersionMiddleware).FullName)
                    _sink.Enqueue($"{logLevel}:{formatter(state, exception)}");
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
