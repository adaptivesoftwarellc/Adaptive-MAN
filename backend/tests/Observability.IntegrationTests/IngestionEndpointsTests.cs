using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Observability.Api.Middleware;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

public class IngestionEndpointsTests : IClassFixture<IngestionWebApplicationFactory>
{
    private readonly IngestionWebApplicationFactory _factory;

    public IngestionEndpointsTests(IngestionWebApplicationFactory factory)
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

    [Fact]
    public async Task Health_Returns200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Events_MissingAuth_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ingest/events", new { @event = "auth_logout", distinct_id = "42" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Events_RevokedKey_Returns401()
    {
        var client = AuthClient(_factory.RevokedKeyPlaintext);
        var response = await client.PostAsJsonAsync("/api/ingest/events", new { @event = "auth_logout", distinct_id = "42" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Events_CorsPreflight_NeedsNoAuth_ReflectsOriginAndAllowsKeyHeader()
    {
        // A browser SDK (PublicClient key) preflights with OPTIONS and no API key. The ingest
        // surface must answer 204, reflect the Origin, and allow the custom X-Observability-Key header.
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Options, "/api/ingest/events");
        req.Headers.Add("Origin", "https://ivr.strategicsolutionsco.com");
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", "x-observability-key");

        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain("https://ivr.strategicsolutionsco.com");
        response.Headers.GetValues("Access-Control-Allow-Headers").Should().ContainMatch("*X-Observability-Key*");
    }

    [Theory]
    [InlineData("auth_login_success")]
    [InlineData("auth_logout")]
    public async Task Events_AuthEvents_Accepted(string eventName)
    {
        var client = AuthClient(_factory.ServerKeyPlaintext);
        var response = await client.PostAsJsonAsync("/api/ingest/events", new
        {
            @event = eventName,
            distinct_id = "42",
            properties = new { release_sha = "a1b2c3d" }
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Events_PageViewed_Accepted_AndPersisted()
    {
        var client = AuthClient(_factory.PublicKeyPlaintext);
        var response = await client.PostAsJsonAsync("/api/ingest/events", new
        {
            @event = "page_viewed",
            distinct_id = "42",
            properties = new { normalized_route = "/patients/:id", feature_area = "patients" }
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var rows = await db.Events.Where(e => e.EventName == "page_viewed").ToListAsync();
        rows.Should().NotBeEmpty();
        rows.Should().Contain(e => e.NormalizedRoute == "/patients/:id" && e.FeatureArea == "patients");
        rows.All(e => e.CorrelationId != null).Should().BeTrue();
    }

    [Fact]
    public async Task Events_ApiRequestFailed_Accepted()
    {
        var client = AuthClient(_factory.ServerKeyPlaintext);
        var response = await client.PostAsJsonAsync("/api/ingest/events", new
        {
            @event = "api_request_failed",
            distinct_id = "42",
            properties = new
            {
                endpoint_group = "orders",
                method = "GET",
                http_status_code = 500,
                is_network_error = false,
                correlation_id = "01HABCXYZ"
            }
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("frontend_exception", "error_type", "source")]
    [InlineData("background_job_failed", "job_name", "error_type")]
    public async Task Events_RequiredFields_AreEnforced(string eventName, string field1, string field2)
    {
        var client = AuthClient(_factory.ServerKeyPlaintext);

        var props = new Dictionary<string, object> { [field1] = "x", [field2] = "y" };
        var ok = await client.PostAsJsonAsync("/api/ingest/events", new
        {
            @event = eventName,
            distinct_id = "system:background-service",
            properties = props
        });
        ok.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var bad = await client.PostAsJsonAsync("/api/ingest/events", new
        {
            @event = eventName,
            distinct_id = "system:background-service",
            properties = new Dictionary<string, object>()
        });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("email", "x@y.com")]
    [InlineData("stack_trace", "at Foo.Bar()")]
    [InlineData("exception_message", "boom")]
    [InlineData("raw_url", "/patients/123?q=1")]
    [InlineData("username", "alice")]
    public async Task Events_ForbiddenField_Returns422_AndWritesSafetyViolation(string field, string value)
    {
        var client = AuthClient(_factory.ServerKeyPlaintext);

        var props = new Dictionary<string, object>
        {
            ["normalized_route"] = "/x",
            [field] = value
        };

        var response = await client.PostAsJsonAsync("/api/ingest/events", new
        {
            @event = "page_viewed",
            distinct_id = "42",
            properties = props
        });
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var violations = await db.SafetyViolations
            .Where(v => v.RejectedField == field).ToListAsync();
        violations.Should().NotBeEmpty();
        violations.All(v => v.Reason == "forbidden_field").Should().BeTrue();
    }

    [Fact]
    public async Task Errors_Server_Accepted_AndAggregates()
    {
        var client = AuthClient(_factory.ServerKeyPlaintext);
        var payload = new
        {
            error_type = "NullReference",
            exception_type = "System.NullReferenceException",
            distinct_id = "system:background-service",
            properties = new
            {
                endpoint_group = "orders",
                http_status_code = 500,
                release_sha = "abc1234"
            }
        };

        for (var i = 0; i < 3; i++)
        {
            var r = await client.PostAsJsonAsync("/api/ingest/errors", payload);
            r.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        var rows = await db.Errors.Where(e => e.ExceptionType == "System.NullReferenceException").ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].OccurrenceCount.Should().Be(3);
    }

    [Fact]
    public async Task CorrelationId_IsEchoed()
    {
        var client = AuthClient(_factory.ServerKeyPlaintext);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/events")
        {
            Content = JsonContent.Create(new { @event = "auth_logout", distinct_id = "42" })
        };
        var corrId = "01HABCXYZ-test";
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, corrId);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Should().Contain(corrId);
    }

    [Fact]
    public async Task SchemaError_MissingEvent_Returns400()
    {
        var client = AuthClient(_factory.ServerKeyPlaintext);
        var response = await client.PostAsJsonAsync("/api/ingest/events", new { distinct_id = "42" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
