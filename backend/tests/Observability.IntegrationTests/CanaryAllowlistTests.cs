using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Observability.Api.Middleware;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

/// <summary>
/// Issue 10.2 — local mirror of the deployed PHI allowlist canary (.github/workflows/canary.yml),
/// plus the dashboard namespacing that keeps canary rows out of real tenants' views.
/// </summary>
public class CanaryAllowlistTests
{
    // The exact payload the canary workflow POSTs: a forbidden PHI field on a valid event.
    private static HttpContent CanaryProbePayload() => JsonContent.Create(new
    {
        @event = "page_viewed",
        distinct_id = "canary-probe",
        properties = new Dictionary<string, object?>
        {
            ["normalized_route"] = "/canary",
            ["email"] = "canary@example.com",
        },
    });

    [Fact]
    public async Task Canary_ForbiddenField_IsRejectedWith422_AndLogsViolation()
    {
        await using var factory = new IngestionWebApplicationFactory();
        await factory.SeedAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthExtensions.HeaderName, factory.ServerKeyPlaintext);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.PostAsync("/api/ingest/events", CanaryProbePayload());

        // This is the canary's contract: a forbidden field must be rejected with 422. A 2xx here
        // is exactly the regression the deployed canary is built to catch.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        (await db.SafetyViolations.AnyAsync(v => v.RejectedField == "email" && v.Reason == "forbidden_field"))
            .Should().BeTrue();
    }

    // Factory that marks the primary seeded app as the canary app, so we can assert it is
    // namespaced out of the dashboard while a real tenant (the second app) remains visible.
    private sealed class CanaryNamespacedFactory : IngestionWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Observability:CanaryApplicationId"] = SeededAppId.ToString(),
                }));
        }
    }

    [Fact]
    public async Task Dashboard_Apps_ExcludesCanaryApp()
    {
        await using var factory = new CanaryNamespacedFactory();
        await factory.SeedAsync();
        // /api/apps requires an authenticated user as of Issue 8.6; Admin sees all (non-canary) apps.
        var client = await factory.BearerClientAsync(factory.AdminEmail, factory.AdminPassword);

        var res = await client.GetAsync("/api/apps");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToList();

        ids.Should().NotContain(factory.SeededAppId, "the canary app must be namespaced out of the dashboard");
        ids.Should().Contain(factory.SecondAppId, "real tenants must still be listed");
    }
}
