using Observability.Domain.Telemetry;

namespace Observability.Application.Ingestion;

/// <summary>
/// Persistence boundary for ingestion. Implemented in Infrastructure (EF Core).
/// Kept as an interface so unit tests can substitute an in-memory store without EF.
/// </summary>
public interface IIngestionStore
{
    Task AddEventAsync(EventRecord record, CancellationToken ct);
    Task UpsertErrorAsync(ErrorRecord record, CancellationToken ct);
    Task AddSafetyViolationAsync(SafetyViolation violation, CancellationToken ct);
    Task UpsertBackgroundJobFailureAsync(BackgroundJobFailure failure, TimeSpan dedupWindow, CancellationToken ct);

    /// <summary>
    /// Resolves the background_job_failed dedup window for an environment (Issue 8.2): the per-env
    /// override when set and positive, otherwise <paramref name="defaultWindow"/>.
    /// </summary>
    Task<TimeSpan> ResolveBackgroundJobDedupWindowAsync(Guid applicationId, Guid environmentId, TimeSpan defaultWindow, CancellationToken ct);
    Task UpsertSessionStartAsync(Session session, CancellationToken ct);
    Task UpsertSessionEndAsync(Guid applicationId, Guid environmentId, string sessionId, DateTime endedAt, CancellationToken ct);
    Task BumpSessionAsync(Guid applicationId, Guid environmentId, string sessionId, DateTime occurredAt, bool isError, CancellationToken ct);
}
