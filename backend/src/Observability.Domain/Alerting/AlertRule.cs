namespace Observability.Domain.Alerting;

/// <summary>
/// A configured alert rule (Issue 8.3). Rules are scoped to an application and, optionally, a single
/// environment (null = every environment of the app). Which parameter fields apply depends on
/// <see cref="RuleType"/>; the per-type meaning is documented on each member.
/// </summary>
public class AlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }

    /// <summary>Environment scope. Null evaluates across every environment of the application.</summary>
    public Guid? EnvironmentId { get; set; }

    public string Name { get; set; } = string.Empty;
    public AlertRuleType RuleType { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// <see cref="AlertRuleType.CountOverWindow"/> only: restrict the count to this event name. Null
    /// counts every event. Ignored by the other rule types.
    /// </summary>
    public string? EventName { get; set; }

    /// <summary>Look-back window the rule evaluates over, in minutes.</summary>
    public int WindowMinutes { get; set; } = 15;

    /// <summary>
    /// Threshold the observed value is compared against. Its unit depends on <see cref="RuleType"/>:
    /// an event count for <see cref="AlertRuleType.CountOverWindow"/>, a percentage for
    /// <see cref="AlertRuleType.ErrorRateAboveThreshold"/>. Unused by the new-error and prod-job types,
    /// which fire on presence rather than a magnitude.
    /// </summary>
    public double Threshold { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the evaluator processed this rule. Null until the first run.</summary>
    public DateTime? LastEvaluatedAt { get; set; }
}
