using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Observability.IntegrationTests;

/// <summary>
/// Issue 8.8 — rate limiting (429 + Retry-After) and the 64 KB ingest payload cap (413).
/// </summary>
public class RateLimitingTests
{
    // Dedicated factory with a tiny permit limit so a burst trips the limiter in a few requests.
    // Separate host instance → isolated limiter + DB state, so it can't perturb other suites.
    private sealed class LowLimitFactory : IngestionWebApplicationFactory
    {
        public const int Permit = 3;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Observability:RateLimiting:PermitLimit"] = Permit.ToString(),
                    ["Observability:RateLimiting:WindowSeconds"] = "60",
                });
            });
        }
    }

    private static HttpClient AuthClient(IngestionWebApplicationFactory factory, string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Observability-Key", key);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static HttpContent AuthLogout() =>
        JsonContent.Create(new { @event = "auth_logout", distinct_id = "rate-limit-probe" });

    [Fact]
    public async Task Ingest_BurstBeyondLimit_Returns429WithRetryAfter()
    {
        await using var factory = new LowLimitFactory();
        await factory.SeedAsync();
        var client = AuthClient(factory, factory.ServerKeyPlaintext);

        // First `Permit` requests succeed within the window.
        for (var i = 0; i < LowLimitFactory.Permit; i++)
        {
            var ok = await client.PostAsync("/api/ingest/events", AuthLogout());
            ok.StatusCode.Should().Be(HttpStatusCode.Accepted, $"request {i + 1} is within the permit limit");
        }

        // The next one is rejected.
        var rejected = await client.PostAsync("/api/ingest/events", AuthLogout());
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull("a 429 must tell the client when to retry");
    }

    [Fact]
    public async Task RateLimit_IsPerKey_OneKeysBurstDoesNotBlockAnother()
    {
        await using var factory = new LowLimitFactory();
        await factory.SeedAsync();
        var server = AuthClient(factory, factory.ServerKeyPlaintext);
        var publicKey = AuthClient(factory, factory.PublicKeyPlaintext);

        // Exhaust the server key's window.
        for (var i = 0; i < LowLimitFactory.Permit; i++)
            (await server.PostAsync("/api/ingest/events", AuthLogout())).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await server.PostAsync("/api/ingest/events", AuthLogout())).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // A different key has its own bucket and is unaffected.
        var other = await publicKey.PostAsync("/api/ingest/events", AuthLogout());
        other.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Ingest_PayloadOver64Kb_Returns413()
    {
        await using var factory = new IngestionWebApplicationFactory();
        await factory.SeedAsync();
        var client = AuthClient(factory, factory.ServerKeyPlaintext);

        // ~100 KB body — comfortably over the 64 KB cap. Rejected before the body is deserialized.
        var big = new string('x', 100 * 1024);
        var payload = JsonContent.Create(new
        {
            @event = "page_viewed",
            distinct_id = "42",
            properties = new Dictionary<string, object?> { ["normalized_route"] = "/x", ["release_sha"] = big },
        });

        var response = await client.PostAsync("/api/ingest/events", payload);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Ingest_NormalPayload_IsAccepted()
    {
        await using var factory = new IngestionWebApplicationFactory();
        await factory.SeedAsync();
        var client = AuthClient(factory, factory.ServerKeyPlaintext);

        var response = await client.PostAsync("/api/ingest/events", AuthLogout());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
