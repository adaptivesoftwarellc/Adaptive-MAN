namespace Observability.Application.Alerting;

/// <summary>
/// Alert evaluator scheduling options (Issue 8.3). Bound from the <c>Observability:Alerting</c>
/// configuration section.
/// </summary>
public sealed class AlertEvaluatorOptions
{
    public const string SectionName = "Observability:Alerting";

    /// <summary>How often the Worker runs the evaluator. Defaults to one minute.</summary>
    public int EvaluationIntervalSeconds { get; set; } = 60;
}
