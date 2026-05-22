namespace Observability.Domain.Audit;

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = string.Empty;
    public string ActorType { get; set; } = string.Empty;
    public Guid? ApplicationId { get; set; }
    public Guid? EnvironmentId { get; set; }
    public string? CorrelationId { get; set; }
    public string DetailsJson { get; set; } = "{}";
}
