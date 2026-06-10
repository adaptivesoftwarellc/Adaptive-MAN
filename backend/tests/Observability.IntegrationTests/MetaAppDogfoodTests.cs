using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Adaptive.ObservabilityClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Observability.Api.Middleware;
using Observability.Domain.Applications;
using Observability.Infrastructure.Authentication;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

/// <summary>
/// Issue 10.8 dogfood. Proves the platform reports its own unhandled server errors as
/// <c>server_error_occurred</c> through the self-pointed SDK: the right (PII-safe) shape is emitted,
/// the ingest surface is excluded (loop guard), and the emitted shape persists as a server-category
/// error under the <c>adaptive-observability-meta</c> app.
///
/// The SDK's HTTP transport is exercised by its own package tests; here a recording
/// <see cref="IAnalyticsService"/> stands in for the client so the platform's emission point
/// (<see cref="ServerErrorTelemetryMiddleware"/>) is asserted deterministically, then the captured
/// payload is replayed through the real ingest endpoint to prove it round-trips and persists.
/// </summary>
public class MetaAppDogfoodTests : IClassFixture<MetaAppDogfoodTests.DogfoodFactory>
{
    private readonly DogfoodFactory _factory;

    public MetaAppDogfoodTests(DogfoodFactory factory) => _factory = factory;

    [Fact]
    public async Task Unhandled_exception_emits_server_error_occurred_with_safe_shape()
    {
        await _factory.SeedMetaAppAsync();
        _factory.Analytics.Reset();

        var client = _factory.CreateClient();
        var res = await client.GetAsync("/api/dev/throw");

        // The 500 response path is unchanged — the middleware re-throws after emitting.
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);

        var call = Assert.Single(_factory.Analytics.Errors);
        Assert.Equal("Exception", call.ErrorType);
        Assert.Equal("System.Exception", call.ExceptionType);
        Assert.Equal("anon", call.DistinctId); // no resolved ingest key on a dev GET

        // Only the catalog-allowed fields — and NEVER the message or stack.
        Assert.Equal("dev", call.Properties!["endpoint_group"]);
        Assert.Equal(500, call.Properties["http_status_code"]);
        Assert.False(string.IsNullOrEmpty((string?)call.Properties["correlation_id"]));
        Assert.DoesNotContain("exception_message", call.Properties.Keys);
        Assert.DoesNotContain("stack_trace", call.Properties.Keys);
        Assert.DoesNotContain("message", call.Properties.Keys);
    }

    [Fact]
    public void Loop_guard_excludes_the_ingest_surface()
    {
        // The SDK delivers server_error_occurred by POSTing to ingest, so an ingest failure must not
        // be reported — otherwise it feeds itself. Everything else is reportable.
        Assert.False(ServerErrorTelemetryMiddleware.EmitsForPath("/api/ingest/errors"));
        Assert.False(ServerErrorTelemetryMiddleware.EmitsForPath("/api/ingest/events"));
        Assert.False(ServerErrorTelemetryMiddleware.EmitsForPath("/api/v1/ingest/errors"));
        Assert.False(ServerErrorTelemetryMiddleware.EmitsForPath("/api/v1/ingest/events"));
        Assert.True(ServerErrorTelemetryMiddleware.EmitsForPath("/api/dev/throw"));
        Assert.True(ServerErrorTelemetryMiddleware.EmitsForPath("/api/dashboard/overview"));
    }

    [Fact]
    public async Task Emitted_server_error_persists_under_the_meta_app()
    {
        await _factory.SeedMetaAppAsync();
        _factory.Analytics.Reset();

        // 1. Force the platform fault → capture exactly what the SDK would have shipped.
        var res = await _factory.CreateClient().GetAsync("/api/dev/throw?kind=invalid");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var call = Assert.Single(_factory.Analytics.Errors);

        // 2. Replay that payload through the real ingest path using the meta-app's server key —
        //    exactly what the dogfood SDK does over HTTP at runtime.
        var ingest = _factory.CreateClient();
        ingest.DefaultRequestHeaders.Add(ApiKeyAuthExtensions.HeaderName, _factory.MetaServerKeyPlaintext);
        var body = new Dictionary<string, object?>
        {
            ["error_type"] = call.ErrorType,
            ["exception_type"] = call.ExceptionType,
            ["distinct_id"] = call.DistinctId,
            ["occurred_at"] = DateTime.UtcNow,
            ["properties"] = call.Properties,
        };
        var ingestRes = await ingest.PostAsJsonAsync("/api/ingest/errors", body);
        Assert.Equal(HttpStatusCode.Accepted, ingestRes.StatusCode);

        // 3. A server-category error row lands under the meta-app.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var rows = await db.Errors.AsNoTracking()
            .Where(e => e.ApplicationId == _factory.MetaAppId)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("InvalidOperationException", row.ErrorType);
        Assert.Equal("System.InvalidOperationException", row.ExceptionType); // server category
        Assert.Equal(500, row.HttpStatusCode);
        Assert.Equal("dev", row.EndpointGroup);
    }

    public sealed record CapturedError(
        string ErrorType, string DistinctId, string? ExceptionType,
        IReadOnlyDictionary<string, object?>? Properties);

    /// <summary>Stands in for the dogfood SDK so the platform's emission point can be asserted.</summary>
    public sealed class RecordingAnalyticsService : IAnalyticsService
    {
        public ConcurrentQueue<CapturedError> Errors { get; } = new();

        public void Capture(string eventName, string distinctId, IReadOnlyDictionary<string, object?>? properties = null) { }

        public void CaptureError(string errorType, string distinctId, string? exceptionType = null, IReadOnlyDictionary<string, object?>? properties = null)
            => Errors.Enqueue(new CapturedError(errorType, distinctId, exceptionType, properties));

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Reset() { while (Errors.TryDequeue(out _)) { } }
    }

    public sealed class DogfoodFactory : IngestionWebApplicationFactory
    {
        public RecordingAnalyticsService Analytics { get; } = new();

        public Guid MetaAppId { get; } = Guid.NewGuid();
        public Guid MetaEnvId { get; } = Guid.NewGuid();
        public string MetaServerKeyPlaintext { get; } = "aoserv_meta_app_key_xxxxxxxxxxxxxxxxxx";

        private int _metaSeeded;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                // Swap the self-pointed SDK for the recorder so emission is observable in-process.
                var descriptor = services.Single(d => d.ServiceType == typeof(IAnalyticsService));
                services.Remove(descriptor);
                services.AddSingleton<IAnalyticsService>(Analytics);
            });
        }

        public async Task SeedMetaAppAsync()
        {
            await SeedAsync();
            if (Interlocked.Exchange(ref _metaSeeded, 1) == 1) return;

            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IApiKeyHasher>();

            db.Applications.Add(new Observability.Domain.Applications.Application
            {
                Id = MetaAppId,
                Name = "Adaptive Observability (meta)",
                Slug = "adaptive-observability-meta",
            });
            db.AppEnvironments.Add(new AppEnvironment
            {
                Id = MetaEnvId,
                ApplicationId = MetaAppId,
                EnvironmentName = "Development",
            });
            db.ApiKeys.Add(new ApiKey
            {
                ApplicationId = MetaAppId,
                EnvironmentId = MetaEnvId,
                KeyHash = hasher.Hash(MetaServerKeyPlaintext),
                KeyType = ApiKeyType.ServerApi,
            });
            await db.SaveChangesAsync();
        }
    }
}
