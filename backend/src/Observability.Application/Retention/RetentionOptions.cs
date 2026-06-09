namespace Observability.Application.Retention;

/// <summary>
/// Platform-wide retention defaults (Issue 8.5). A per-environment override on
/// <c>AppEnvironment</c> takes precedence when set; these apply otherwise. Bound from the
/// <c>Observability:Retention</c> configuration section.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Observability:Retention";

    /// <summary>Default age cap for <c>Events</c> rows, by <c>CreatedAt</c>.</summary>
    public int EventRetentionDays { get; set; } = 90;

    /// <summary>Default age cap for <c>Errors</c> rows, by <c>LastSeenAt</c>.</summary>
    public int ErrorRetentionDays { get; set; } = 180;

    /// <summary>
    /// Age cap for <c>AuditLogs</c> rows, by <c>OccurredAt</c>. Enforces the 365-day audit retention
    /// defined in 8.7/PR C. Global — not per-environment.
    /// </summary>
    public int AuditLogRetentionDays { get; set; } = 365;

    /// <summary>UTC time-of-day the Worker runs the nightly sweep. <c>HH:mm</c>.</summary>
    public string DailyRunAtUtc { get; set; } = "03:00";

    /// <summary>Rows deleted per SaveChanges so a large backlog doesn't build one giant transaction.</summary>
    public int DeleteBatchSize { get; set; } = 1000;
}
