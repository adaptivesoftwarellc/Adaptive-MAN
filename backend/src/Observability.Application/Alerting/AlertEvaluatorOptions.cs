namespace Observability.Application.Alerting;

/// <summary>
/// Alert evaluator scheduling options (Issue 8.3). Bound from the <c>Observability:Alerting</c>
/// configuration section.
/// </summary>
public sealed class AlertEvaluatorOptions
{
    public const string SectionName = "Observability:Alerting";

    /// <summary>
    /// Whether the alert evaluation host runs. Defaults to <c>false</c>: the evaluator polls the DB on
    /// a short interval, which would keep a serverless (auto-pause) database awake 24/7. Enable it
    /// per-environment once a tenant is live and there is telemetry to evaluate (the same go-live step
    /// that turns the prod DB's auto-pause off).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>How often the evaluator runs. Defaults to one minute.</summary>
    public int EvaluationIntervalSeconds { get; set; } = 60;
}
