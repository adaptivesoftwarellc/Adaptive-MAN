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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}
