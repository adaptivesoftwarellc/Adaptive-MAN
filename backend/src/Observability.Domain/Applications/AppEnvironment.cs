namespace Observability.Domain.Applications;

public class AppEnvironment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public Application? Application { get; set; }
    public string EnvironmentName { get; set; } = string.Empty;
    public bool ReplayEnabled { get; set; }
    public string AllowedOriginsJson { get; set; } = "[]";

    /// <summary>
    /// Per-environment override for the background_job_failed alert dedup window (Issue 8.2).
    /// Null inherits the platform default (<c>IngestionService.BackgroundJobDedupWindow</c>).
    /// </summary>
    public int? BackgroundJobDedupWindowMinutes { get; set; }

    /// <summary>
    /// Per-environment retention overrides (Issue 8.5). Null inherits the platform default from
    /// <c>RetentionOptions</c>. <see cref="ReplayRetentionDays"/> is reserved for Phase 9 replay and
    /// not yet enforced by the sweep.
    /// </summary>
    public int? EventRetentionDays { get; set; }
    public int? ErrorRetentionDays { get; set; }
    public int? ReplayRetentionDays { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}
