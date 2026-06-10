namespace Observability.Domain.Alerting;

/// <summary>
/// A single instance of an <see cref="AlertRule"/> firing (Issue 8.3). Until notifications land in 8.4
/// (blocked on the ACS-vs-SendGrid decision) the engine is visibility-only: it persists fired alerts
/// here so the dashboard can surface them, but does not deliver anything externally.
///
/// <see cref="DedupKey"/> identifies the logical alert within a rule so the evaluator can suppress
/// re-firing the same condition on every pass — a row is only written when no prior row shares the
/// (<see cref="AlertRuleId"/>, <see cref="DedupKey"/>) pair inside the rule's window.
/// </summary>
public class FiredAlert
{
    public long Id { get; set; }
    public Guid AlertRuleId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid? EnvironmentId { get; set; }
    public AlertRuleType RuleType { get; set; }
    public DateTime FiredAt { get; set; }
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>The measured value that tripped the rule (count, percentage, or occurrence count).</summary>
    public double ObservedValue { get; set; }

    /// <summary>The rule's configured threshold at fire time, copied for historical clarity.</summary>
    public double Threshold { get; set; }

    public string Summary { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
}
