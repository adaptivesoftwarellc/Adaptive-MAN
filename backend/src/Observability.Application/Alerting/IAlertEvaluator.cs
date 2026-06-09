namespace Observability.Application.Alerting;

/// <summary>
/// Evaluates every enabled <c>AlertRule</c> against current telemetry and persists any that fire
/// (Issue 8.3). Runs on an interval from the Worker, but is a plain injectable service so it can be
/// invoked and asserted directly in tests. Visibility-only until 8.4 notifications land — it writes
/// <c>FiredAlerts</c> rows and never delivers anything externally.
/// </summary>
public interface IAlertEvaluator
{
    Task<AlertEvaluationResult> EvaluateAsync(CancellationToken ct);
}

/// <param name="RulesEvaluated">Number of enabled rules processed this pass.</param>
/// <param name="AlertsFired">Number of new <c>FiredAlerts</c> rows written this pass (after dedup).</param>
public sealed record AlertEvaluationResult(int RulesEvaluated, int AlertsFired);
