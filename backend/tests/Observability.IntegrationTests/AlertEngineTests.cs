using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Observability.Application.Alerting;
using Observability.Domain.Alerting;
using Observability.Domain.Telemetry;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

// Own factory per test (not a shared IClassFixture) so each gets an isolated InMemory database — the
// evaluator mutates global state (FiredAlerts), so cross-test bleed would be flaky. Mirrors RetentionSweepTests.
public class AlertEngineTests : IAsyncLifetime, IDisposable
{
    private readonly IngestionWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.SeedAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _factory.Dispose();

    private async Task<AlertEvaluationResult> EvaluateAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<IAlertEvaluator>();
        return await evaluator.EvaluateAsync(CancellationToken.None);
    }

    private async Task AddAsync(params object[] entities)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private async Task<List<FiredAlert>> FiredAlertsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
        return await db.FiredAlerts.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task CountOverWindow_fires_when_count_meets_threshold_and_not_below()
    {
        var now = DateTime.UtcNow;
        await AddAsync(
            MakeRule(AlertRuleType.CountOverWindow, threshold: 3, eventName: "page_viewed", windowMinutes: 15),
            MakeEvent("page_viewed", now.AddMinutes(-1)),
            MakeEvent("page_viewed", now.AddMinutes(-2)),
            MakeEvent("page_viewed", now.AddMinutes(-3)),
            MakeEvent("page_viewed", now.AddMinutes(-30)),  // outside the window — not counted
            MakeEvent("other_event", now.AddMinutes(-1)));  // wrong name — not counted

        var result = await EvaluateAsync();

        result.RulesEvaluated.Should().Be(1);
        result.AlertsFired.Should().Be(1);

        var alerts = await FiredAlertsAsync();
        alerts.Should().ContainSingle();
        alerts[0].RuleType.Should().Be(AlertRuleType.CountOverWindow);
        alerts[0].ObservedValue.Should().Be(3);
    }

    [Fact]
    public async Task CountOverWindow_does_not_fire_below_threshold()
    {
        var now = DateTime.UtcNow;
        await AddAsync(
            MakeRule(AlertRuleType.CountOverWindow, threshold: 5, eventName: "page_viewed"),
            MakeEvent("page_viewed", now.AddMinutes(-1)),
            MakeEvent("page_viewed", now.AddMinutes(-2)));

        var result = await EvaluateAsync();

        result.AlertsFired.Should().Be(0);
        (await FiredAlertsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task NewErrorAfterRelease_fires_per_new_release_error_only()
    {
        var now = DateTime.UtcNow;
        await AddAsync(
            MakeRule(AlertRuleType.NewErrorAfterRelease, windowMinutes: 60),
            MakeError("FreshOnRelease", firstSeenAt: now.AddMinutes(-5), releaseSha: "abc1234"), // new + release -> fires
            MakeError("NoRelease", firstSeenAt: now.AddMinutes(-5), releaseSha: null),            // no release -> ignored
            MakeError("OldError", firstSeenAt: now.AddHours(-3), releaseSha: "abc1234"));         // outside window -> ignored

        var result = await EvaluateAsync();

        result.AlertsFired.Should().Be(1);
        var alerts = await FiredAlertsAsync();
        alerts.Should().ContainSingle();
        alerts[0].RuleType.Should().Be(AlertRuleType.NewErrorAfterRelease);
        alerts[0].Summary.Should().Contain("abc1234");
    }

    [Fact]
    public async Task ErrorRateAboveThreshold_fires_when_rate_meets_threshold()
    {
        var now = DateTime.UtcNow;
        // 2 active errors / 4 events = 50%, threshold 40% -> fires.
        await AddAsync(
            MakeRule(AlertRuleType.ErrorRateAboveThreshold, threshold: 40, windowMinutes: 15),
            MakeEvent("e", now.AddMinutes(-1)), MakeEvent("e", now.AddMinutes(-2)),
            MakeEvent("e", now.AddMinutes(-3)), MakeEvent("e", now.AddMinutes(-4)),
            MakeError("A", firstSeenAt: now.AddMinutes(-5), releaseSha: null, lastSeenAt: now.AddMinutes(-1)),
            MakeError("B", firstSeenAt: now.AddMinutes(-5), releaseSha: null, lastSeenAt: now.AddMinutes(-2)));

        var result = await EvaluateAsync();

        result.AlertsFired.Should().Be(1);
        var alerts = await FiredAlertsAsync();
        alerts.Should().ContainSingle().Which.ObservedValue.Should().BeApproximately(50, 0.01);
    }

    [Fact]
    public async Task ErrorRateAboveThreshold_does_not_fire_without_traffic()
    {
        var now = DateTime.UtcNow;
        // Errors but zero events in window — rate is undefined, must not fire (avoids div-by-zero noise).
        await AddAsync(
            MakeRule(AlertRuleType.ErrorRateAboveThreshold, threshold: 1, windowMinutes: 15),
            MakeError("A", firstSeenAt: now.AddMinutes(-5), releaseSha: null, lastSeenAt: now.AddMinutes(-1)));

        var result = await EvaluateAsync();

        result.AlertsFired.Should().Be(0);
    }

    [Fact]
    public async Task AnyProdJobFailure_fires_only_for_production_environments()
    {
        var now = DateTime.UtcNow;
        // Rule on the second app, which has a Production environment seeded by the factory.
        await AddAsync(
            MakeRule(AlertRuleType.AnyProdJobFailure, windowMinutes: 30, appId: _factory.SecondAppId, envId: null),
            MakeJobFailure(_factory.SecondAppId, _factory.SecondEnvId, "NightlyExport", now.AddMinutes(-5)));
        // A failure in the (non-prod) first app must not trip the prod rule.
        await AddAsync(MakeJobFailure(_factory.SeededAppId, _factory.SeededEnvId, "DevJob", now.AddMinutes(-5)));

        var result = await EvaluateAsync();

        result.AlertsFired.Should().Be(1);
        var alerts = await FiredAlertsAsync();
        alerts.Should().ContainSingle();
        alerts[0].ApplicationId.Should().Be(_factory.SecondAppId);
        alerts[0].Summary.Should().Contain("NightlyExport");
    }

    [Fact]
    public async Task Evaluator_dedupes_so_a_second_pass_does_not_refire()
    {
        var now = DateTime.UtcNow;
        await AddAsync(
            MakeRule(AlertRuleType.CountOverWindow, threshold: 2, eventName: "page_viewed"),
            MakeEvent("page_viewed", now.AddMinutes(-1)),
            MakeEvent("page_viewed", now.AddMinutes(-2)));

        (await EvaluateAsync()).AlertsFired.Should().Be(1);
        (await EvaluateAsync()).AlertsFired.Should().Be(0); // same window + dedup key -> suppressed

        (await FiredAlertsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Disabled_rules_are_skipped()
    {
        var now = DateTime.UtcNow;
        var rule = MakeRule(AlertRuleType.CountOverWindow, threshold: 1, eventName: "page_viewed");
        rule.IsEnabled = false;
        await AddAsync(rule, MakeEvent("page_viewed", now.AddMinutes(-1)));

        var result = await EvaluateAsync();

        result.RulesEvaluated.Should().Be(0);
        result.AlertsFired.Should().Be(0);
    }

    // Rules are left app-wide (EnvironmentId null). The seeded app has a single environment, so
    // app-wide scoping resolves to it for the count/error rules, and to the Production env for the
    // prod-job rule — exercising ResolveEnvironmentIdsAsync without threading env ids through callers.
    private AlertRule MakeRule(
        AlertRuleType type, double threshold = 0, string? eventName = null, int windowMinutes = 15,
        Guid? appId = null, Guid? envId = null)
        => new()
        {
            ApplicationId = appId ?? _factory.SeededAppId,
            EnvironmentId = envId,
            Name = $"{type} rule",
            RuleType = type,
            IsEnabled = true,
            EventName = eventName,
            WindowMinutes = windowMinutes,
            Threshold = threshold,
        };

    private EventRecord MakeEvent(string name, DateTime createdAt) => new()
    {
        ApplicationId = _factory.SeededAppId,
        EnvironmentId = _factory.SeededEnvId,
        EventName = name,
        DistinctId = "u_test",
        PropertiesJson = "{}",
        OccurredAt = createdAt,
        CreatedAt = createdAt,
    };

    private ErrorRecord MakeError(string errorType, DateTime firstSeenAt, string? releaseSha, DateTime? lastSeenAt = null) => new()
    {
        ApplicationId = _factory.SeededAppId,
        EnvironmentId = _factory.SeededEnvId,
        Fingerprint = Guid.NewGuid().ToString("N")[..32],
        ErrorType = errorType,
        ReleaseSha = releaseSha,
        PropertiesJson = "{}",
        OccurrenceCount = 1,
        FirstSeenAt = firstSeenAt,
        LastSeenAt = lastSeenAt ?? firstSeenAt,
    };

    private static BackgroundJobFailure MakeJobFailure(Guid appId, Guid envId, string jobName, DateTime lastSeenAt) => new()
    {
        ApplicationId = appId,
        EnvironmentId = envId,
        JobName = jobName,
        ErrorType = "TimeoutException",
        Fingerprint = Guid.NewGuid().ToString("N")[..32],
        OccurrenceCount = 1,
        FirstSeenAt = lastSeenAt.AddMinutes(-1),
        LastSeenAt = lastSeenAt,
    };
}
