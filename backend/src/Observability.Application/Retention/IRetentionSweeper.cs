namespace Observability.Application.Retention;

/// <summary>
/// Deletes telemetry past its retention window (Issue 8.5). Runs nightly from the Worker, but is a
/// plain injectable service so it can be invoked and asserted directly in tests. Each run writes a
/// single <c>admin.retention.swept</c> audit row recording what it removed.
/// </summary>
public interface IRetentionSweeper
{
    Task<RetentionSweepResult> SweepAsync(CancellationToken ct);
}

/// <param name="EventsDeleted">Total <c>Events</c> rows removed across all environments.</param>
/// <param name="ErrorsDeleted">Total <c>Errors</c> rows removed across all environments.</param>
/// <param name="AuditLogsDeleted">Audit rows removed by the global 365-day cap.</param>
/// <param name="EnvironmentsSwept">Number of environments evaluated.</param>
public sealed record RetentionSweepResult(
    long EventsDeleted,
    long ErrorsDeleted,
    long AuditLogsDeleted,
    int EnvironmentsSwept);
