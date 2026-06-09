namespace Observability.Domain.Telemetry;

/// <summary>
/// Specialized incident table for background_job_failed errors.
/// Sidecar to ErrorRecord — every BG job failure also creates/upserts a row here so the
/// dashboard can show "job-level" incidents distinct from per-route errors, and so the
/// alert engine (Phase 8) can rate-limit on (JobName, ErrorType) directly.
///
/// Within <see cref="OccurrenceCount"/> bumps inside the configured window,
/// <see cref="LastSuppressedAt"/> records the most recent suppressed duplicate so
/// downstream alerting can reason about flapping.
/// </summary>
public class BackgroundJobFailure
{
    public long Id { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string ErrorType { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public long OccurrenceCount { get; set; } = 1;

    /// <summary>
    /// Count of duplicate failures that arrived inside the dedup window and were collapsed onto this
    /// incident without re-alerting (Issue 8.2). <c>OccurrenceCount - SuppressedCount - 1</c> is the
    /// number of alert-worthy recurrences (the first occurrence plus any that landed after a window
    /// had elapsed). Surfaced in the dashboard so operators can see flapping volume at a glance.
    /// </summary>
    public long SuppressedCount { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? LastSuppressedAt { get; set; }
    public string? ReleaseSha { get; set; }
}
