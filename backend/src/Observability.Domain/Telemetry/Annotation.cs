namespace Observability.Domain.Telemetry;

/// <summary>
/// A point-in-time marker rendered on dashboard charts — typically a deploy. Scoped to an
/// application + environment so tenants never see each other's markers.
/// </summary>
public class Annotation
{
    public long Id { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid EnvironmentId { get; set; }
    public DateTime At { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? ReleaseSha { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
