namespace Observability.Domain.Applications;

public enum ApiKeyType
{
    PublicClient = 1,
    ServerApi = 2
}

public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string KeyHash { get; set; } = string.Empty;
    public ApiKeyType KeyType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Issue 10.6 — last time this key successfully authenticated a request. Surfaced read-only in the
    /// admin Keys page. Stamped by <c>ApiKeyResolver</c> on a throttle (not on every ingest call) so the
    /// hot path takes at most one extra write per key per few minutes.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }
    public string? CreatedByUserId { get; set; }

    public bool IsActive(DateTime now) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}
