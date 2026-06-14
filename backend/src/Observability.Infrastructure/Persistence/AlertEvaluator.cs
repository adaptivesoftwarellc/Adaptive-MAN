using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Observability.Application.Alerting;
using Observability.Domain.Alerting;

namespace Observability.Infrastructure.Persistence;

/// <summary>
/// Evaluates each enabled <see cref="AlertRule"/> against telemetry and persists fired alerts
/// (Issue 8.3). Visibility-only: it writes <see cref="FiredAlert"/> rows and relies on the dashboard
/// to surface them — external delivery waits on 8.4 notifications.
///
/// Queries load rows rather than using set-based aggregates so behavior is identical on the InMemory
/// provider the tests run against; rule volumes here are small (operator-authored), not bulk.
/// </summary>
public sealed class AlertEvaluator : IAlertEvaluator
{
    private const string ProductionEnvironmentName = "Production";

    private readonly ObservabilityDbContext _db;

    public AlertEvaluator(ObservabilityDbContext db) => _db = db;

    public async Task<AlertEvaluationResult> EvaluateAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rules = await _db.AlertRules.Where(r => r.IsEnabled).ToListAsync(ct);

        var fired = 0;
        foreach (var rule in rules)
        {
            var windowStart = now.AddMinutes(-(rule.WindowMinutes > 0 ? rule.WindowMinutes : 15));

            var candidates = rule.RuleType switch
            {
                AlertRuleType.CountOverWindow => await EvaluateCountOverWindowAsync(rule, windowStart, now, ct),
                AlertRuleType.NewErrorAfterRelease => await EvaluateNewErrorAfterReleaseAsync(rule, windowStart, now, ct),
                AlertRuleType.ErrorRateAboveThreshold => await EvaluateErrorRateAsync(rule, windowStart, now, ct),
                AlertRuleType.AnyProdJobFailure => await EvaluateAnyProdJobFailureAsync(rule, windowStart, now, ct),
                _ => new List<FiredAlert>(),
            };

            foreach (var alert in candidates)
            {
                // Suppress the same logical alert re-firing on every pass. NewErrorAfterRelease must fire
                // exactly once per (fingerprint, release) — ever — so a brand-new error doesn't re-alert
                // each window it stays inside; its dedup key already encodes the release. Every other rule
                // type dedups within the rule window, so a standing condition re-notifies once per window
                // rather than on every 60s pass.
                bool alreadyFired;
                if (rule.RuleType == AlertRuleType.NewErrorAfterRelease)
                {
                    alreadyFired = await _db.FiredAlerts.AnyAsync(
                        f => f.AlertRuleId == rule.Id && f.DedupKey == alert.DedupKey, ct);
                }
                else
                {
                    alreadyFired = await _db.FiredAlerts.AnyAsync(
                        f => f.AlertRuleId == rule.Id && f.DedupKey == alert.DedupKey && f.FiredAt >= windowStart, ct);
                }
                if (alreadyFired) continue;

                _db.FiredAlerts.Add(alert);
                fired++;
            }

            rule.LastEvaluatedAt = now;
        }

        if (rules.Count > 0)
            await _db.SaveChangesAsync(ct);

        return new AlertEvaluationResult(rules.Count, fired);
    }

    private async Task<List<FiredAlert>> EvaluateCountOverWindowAsync(AlertRule rule, DateTime windowStart, DateTime now, CancellationToken ct)
    {
        var envIds = await ResolveEnvironmentIdsAsync(rule, productionOnly: false, ct);
        if (envIds.Count == 0) return new();

        var query = _db.Events.Where(e =>
            e.ApplicationId == rule.ApplicationId && envIds.Contains(e.EnvironmentId) && e.CreatedAt >= windowStart);
        if (!string.IsNullOrWhiteSpace(rule.EventName))
            query = query.Where(e => e.EventName == rule.EventName);

        var count = await query.CountAsync(ct);
        if (count < rule.Threshold) return new();

        var label = string.IsNullOrWhiteSpace(rule.EventName) ? "events" : $"'{rule.EventName}' events";
        return new()
        {
            BuildAlert(rule, now, dedupKey: $"count:{rule.EventName ?? "*"}", observed: count,
                summary: $"{count} {label} in the last {rule.WindowMinutes}m (threshold {rule.Threshold:0.##}).",
                details: new { event_name = rule.EventName, count, window_minutes = rule.WindowMinutes }),
        };
    }

    private async Task<List<FiredAlert>> EvaluateNewErrorAfterReleaseAsync(AlertRule rule, DateTime windowStart, DateTime now, CancellationToken ct)
    {
        var envIds = await ResolveEnvironmentIdsAsync(rule, productionOnly: false, ct);
        if (envIds.Count == 0) return new();

        // A "new error after release" is a fingerprint first seen inside the window that carries a
        // release SHA — i.e. a brand-new error attributable to a deploy.
        var newErrors = await _db.Errors.Where(e =>
                e.ApplicationId == rule.ApplicationId && envIds.Contains(e.EnvironmentId)
                && e.FirstSeenAt >= windowStart && e.ReleaseSha != null)
            .ToListAsync(ct);

        return newErrors.Select(e => BuildAlert(rule, now,
            dedupKey: $"newerr:{e.EnvironmentId}:{e.Fingerprint}:{e.ReleaseSha}",
            observed: e.OccurrenceCount,
            environmentId: e.EnvironmentId,
            summary: $"New error '{e.ErrorType}' ({e.OccurrenceCount}x) first seen on release {e.ReleaseSha}.",
            details: new { e.ErrorType, e.Fingerprint, e.ReleaseSha, e.OccurrenceCount, first_seen_at = e.FirstSeenAt }))
            .ToList();
    }

    private async Task<List<FiredAlert>> EvaluateErrorRateAsync(AlertRule rule, DateTime windowStart, DateTime now, CancellationToken ct)
    {
        var envIds = await ResolveEnvironmentIdsAsync(rule, productionOnly: false, ct);
        if (envIds.Count == 0) return new();

        // Approximation suitable for visibility-only alerting: active distinct error fingerprints in the
        // window as a percentage of events ingested in the window. Skip when there's no traffic to rate.
        var eventCount = await _db.Events.CountAsync(e =>
            e.ApplicationId == rule.ApplicationId && envIds.Contains(e.EnvironmentId) && e.CreatedAt >= windowStart, ct);
        if (eventCount == 0) return new();

        var errorCount = await _db.Errors.CountAsync(e =>
            e.ApplicationId == rule.ApplicationId && envIds.Contains(e.EnvironmentId) && e.LastSeenAt >= windowStart, ct);

        var ratePercent = 100.0 * errorCount / eventCount;
        if (ratePercent < rule.Threshold) return new();

        return new()
        {
            BuildAlert(rule, now, dedupKey: "errorrate", observed: ratePercent,
                summary: $"Error rate {ratePercent:0.##}% over the last {rule.WindowMinutes}m ({errorCount} errors / {eventCount} events, threshold {rule.Threshold:0.##}%).",
                details: new { rate_percent = ratePercent, error_count = errorCount, event_count = eventCount, window_minutes = rule.WindowMinutes }),
        };
    }

    private async Task<List<FiredAlert>> EvaluateAnyProdJobFailureAsync(AlertRule rule, DateTime windowStart, DateTime now, CancellationToken ct)
    {
        var envIds = await ResolveEnvironmentIdsAsync(rule, productionOnly: true, ct);
        if (envIds.Count == 0) return new();

        var failures = await _db.BackgroundJobFailures.Where(j =>
                j.ApplicationId == rule.ApplicationId && envIds.Contains(j.EnvironmentId) && j.LastSeenAt >= windowStart)
            .ToListAsync(ct);

        return failures.Select(j => BuildAlert(rule, now,
            dedupKey: $"prodjob:{j.EnvironmentId}:{j.Fingerprint}",
            observed: j.OccurrenceCount,
            environmentId: j.EnvironmentId,
            summary: $"Production job '{j.JobName}' failed ({j.ErrorType}, {j.OccurrenceCount}x).",
            details: new { j.JobName, j.ErrorType, j.Fingerprint, j.OccurrenceCount, last_seen_at = j.LastSeenAt }))
            .ToList();
    }

    /// <summary>
    /// Resolves the environment ids a rule evaluates over: the single scoped environment when
    /// <see cref="AlertRule.EnvironmentId"/> is set, otherwise every environment of the application.
    /// When <paramref name="productionOnly"/> is set the result is further limited to environments
    /// named <c>Production</c>.
    /// </summary>
    private async Task<List<Guid>> ResolveEnvironmentIdsAsync(AlertRule rule, bool productionOnly, CancellationToken ct)
    {
        var query = _db.AppEnvironments.AsNoTracking().Where(e => e.ApplicationId == rule.ApplicationId);
        if (rule.EnvironmentId.HasValue)
            query = query.Where(e => e.Id == rule.EnvironmentId.Value);
        if (productionOnly)
            query = query.Where(e => e.EnvironmentName == ProductionEnvironmentName);
        return await query.Select(e => e.Id).ToListAsync(ct);
    }

    private static FiredAlert BuildAlert(
        AlertRule rule, DateTime now, string dedupKey, double observed, string summary, object details, Guid? environmentId = null)
        => new()
        {
            AlertRuleId = rule.Id,
            ApplicationId = rule.ApplicationId,
            EnvironmentId = environmentId ?? rule.EnvironmentId,
            RuleType = rule.RuleType,
            FiredAt = now,
            DedupKey = dedupKey,
            ObservedValue = observed,
            Threshold = rule.Threshold,
            Summary = summary,
            DetailsJson = JsonSerializer.Serialize(details),
        };
}
