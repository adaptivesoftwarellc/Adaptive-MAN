using Microsoft.EntityFrameworkCore;
using Observability.Domain.Applications;
using Observability.Infrastructure.Persistence;

namespace Observability.Infrastructure.Authentication;

public sealed record ResolvedApiKey(Guid ApplicationId, Guid EnvironmentId, ApiKeyType KeyType);

public interface IApiKeyResolver
{
    Task<ResolvedApiKey?> ResolveAsync(string plaintextKey, CancellationToken ct);
}

public sealed class ApiKeyResolver : IApiKeyResolver
{
    // Throttle LastUsedAt writes so a busy key doesn't add a write to every ingest call. One stamp per
    // key per window is plenty for the "last used" admin display (Issue 10.6).
    private static readonly TimeSpan LastUsedStampInterval = TimeSpan.FromMinutes(5);

    private readonly ObservabilityDbContext _db;
    private readonly IApiKeyHasher _hasher;

    public ApiKeyResolver(ObservabilityDbContext db, IApiKeyHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<ResolvedApiKey?> ResolveAsync(string plaintextKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plaintextKey)) return null;

        var hash = _hasher.Hash(plaintextKey);
        var now = DateTime.UtcNow;

        var key = await _db.ApiKeys
            .AsNoTracking()
            .Where(k => k.KeyHash == hash)
            .Where(k => k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now))
            .Select(k => new { k.Id, k.ApplicationId, k.EnvironmentId, k.KeyType, k.LastUsedAt })
            .FirstOrDefaultAsync(ct);

        if (key is null) return null;

        await StampLastUsedAsync(key.Id, key.LastUsedAt, now, ct);

        return new ResolvedApiKey(key.ApplicationId, key.EnvironmentId, key.KeyType);
    }

    private async Task StampLastUsedAsync(Guid keyId, DateTime? lastUsedAt, DateTime now, CancellationToken ct)
    {
        if (lastUsedAt is { } prev && now - prev < LastUsedStampInterval) return;

        try
        {
            // Re-check the throttle in the WHERE clause so a burst of concurrent requests that all read the
            // same stale LastUsedAt collapses to a single write — only the first past the cutoff matches.
            var cutoff = now - LastUsedStampInterval;
            await _db.ApiKeys
                .Where(k => k.Id == keyId && (k.LastUsedAt == null || k.LastUsedAt < cutoff))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Last-used is best-effort telemetry; a failed stamp must never reject an otherwise valid key.
        }
    }
}
