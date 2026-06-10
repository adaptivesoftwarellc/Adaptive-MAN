using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Observability.Application.Alerting;
using Observability.Domain.Alerting;
using Observability.Domain.Telemetry;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

/// <summary>
/// Pins GET /api/dashboard/alerts — the read consumer for the visibility-only alert engine (Issue
/// 8.3). Verifies the rule-name join, that an app-wide rule's null-env alert surfaces under an env
/// view, and that the read is authenticated like the rest of the dashboard.
/// </summary>
public class DashboardAlertsTests : IClassFixture<IngestionWebApplicationFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IngestionWebApplicationFactory _factory;

    public DashboardAlertsTests(IngestionWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Alerts_endpoint_returns_fired_alerts_with_rule_name()
    {
        await _factory.SeedAsync();

        var now = DateTime.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();

            // App-wide rule (null env) so its fired alert must still surface under the env view.
            var rule = new AlertRule
            {
                ApplicationId = _factory.SeededAppId,
                EnvironmentId = null,
                Name = "Login spike",
                RuleType = AlertRuleType.CountOverWindow,
                EventName = "auth_login_success",
                WindowMinutes = 15,
                Threshold = 100,
            };
            db.AlertRules.Add(rule);
            db.FiredAlerts.Add(new FiredAlert
            {
                AlertRuleId = rule.Id,
                ApplicationId = _factory.SeededAppId,
                EnvironmentId = null,
                RuleType = AlertRuleType.CountOverWindow,
                FiredAt = now.AddMinutes(-2),
                DedupKey = "count:auth_login_success",
                ObservedValue = 142,
                Threshold = 100,
                Summary = "142 'auth_login_success' events in the last 15m (threshold 100).",
                DetailsJson = "{}",
            });
            await db.SaveChangesAsync();
        }

        var dashboard = await _factory.BearerClientAsync(_factory.AdminEmail, _factory.AdminPassword);
        var res = await dashboard.GetAsync($"/api/dashboard/alerts?app={_factory.SeededAppId}&env={_factory.SeededEnvId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = JsonSerializer.Deserialize<AlertsResponse>(await res.Content.ReadAsStringAsync(), Json)!;
        Assert.Equal(1, body.Total);
        var row = Assert.Single(body.Rows);
        Assert.Equal("Login spike", row.RuleName);
        Assert.Equal("CountOverWindow", row.RuleType);
        Assert.Equal(142, row.ObservedValue);
        Assert.Null(row.EnvironmentId); // app-wide alert surfaced under the env view
    }

    [Fact]
    public async Task Alerts_endpoint_requires_authentication()
    {
        await _factory.SeedAsync();
        var anon = _factory.CreateClient();
        var res = await anon.GetAsync($"/api/dashboard/alerts?app={_factory.SeededAppId}&env={_factory.SeededEnvId}");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    private sealed record AlertsResponse(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("rows")] List<AlertRow> Rows);

    private sealed record AlertRow(
        [property: JsonPropertyName("rule_name")] string? RuleName,
        [property: JsonPropertyName("rule_type")] string RuleType,
        [property: JsonPropertyName("observed_value")] double ObservedValue,
        [property: JsonPropertyName("environment_id")] Guid? EnvironmentId);
}
