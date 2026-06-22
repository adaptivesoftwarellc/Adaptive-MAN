using System.Data;
using Microsoft.EntityFrameworkCore;
using Observability.Application.Ingestion;

namespace Observability.Infrastructure.Persistence;

public sealed class ErrorFingerprintBackfiller : IErrorFingerprintBackfiller
{
    private readonly ObservabilityDbContext _db;

    public ErrorFingerprintBackfiller(ObservabilityDbContext db) => _db = db;

    public async Task<FingerprintBackfillResult> BackfillAsync(int batchSize, CancellationToken ct)
    {
        if (batchSize <= 0) batchSize = 500;
        var target = ErrorFingerprint.CurrentVersion;
        int scanned = 0, updated = 0, merged = 0;
        var relational = _db.Database.IsRelational();

        while (true)
        {
            // Each batch runs under a serializable transaction (on relational providers) so concurrent
            // ingestion can't (a) bump OccurrenceCount on a row we're about to merge+remove — losing that
            // increment — or (b) insert a fresh row at the target fingerprint between our canonical lookup
            // and SaveChanges, which would trip the unique (ApplicationId, EnvironmentId, Fingerprint)
            // index and abort the whole backfill. The retrying execution strategy wraps BeginTransaction
            // (required when EnableRetryOnFailure is configured); ChangeTracker.Clear() makes each attempt
            // re-read clean. InMemory (tests) has no transaction support, so it takes the plain path.
            var strategy = _db.Database.CreateExecutionStrategy();
            var batchResult = await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();

                if (!relational)
                    return await ProcessBatchAsync(target, batchSize, ct);

                await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var r = await ProcessBatchAsync(target, batchSize, ct);
                await tx.CommitAsync(ct);
                return r;
            });

            if (batchResult.Scanned == 0) break;
            scanned += batchResult.Scanned;
            updated += batchResult.Updated;
            merged += batchResult.Merged;
        }

        return new FingerprintBackfillResult(scanned, updated, merged, target);
    }

    private async Task<BatchOutcome> ProcessBatchAsync(int target, int batchSize, CancellationToken ct)
    {
        int updated = 0, merged = 0;

        // Order by Id so each pass consumes the next slice; rows are removed or advanced past the
        // version filter within the loop, so we never revisit a processed row.
        var batch = await _db.Errors
            .Where(e => e.FingerprintVersion < target)
            .OrderBy(e => e.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return new BatchOutcome(0, 0, 0);

        {
            // Two stale rows can recompute to the *same* fingerprint; that's exactly the collision the
            // version bump is meant to collapse. Group by the target key per tenant/env so each group
            // resolves to a single surviving row — otherwise we'd re-stamp both to the same fingerprint
            // and trip the unique (ApplicationId, EnvironmentId, Fingerprint) index on SaveChanges.
            var batchIds = batch.Select(e => e.Id).ToHashSet();
            var groups = batch.GroupBy(e => (
                e.ApplicationId,
                e.EnvironmentId,
                Fingerprint: ErrorFingerprint.Compute(e.ErrorType, e.ExceptionType, e.EndpointGroup, e.JobName)));

            foreach (var group in groups)
            {
                var (appId, envId, fingerprint) = group.Key;
                var rows = group.ToList();

                // Prefer an already-persisted canonical outside this batch (e.g. a current-version row
                // that this stale row now hashes onto); otherwise elect the lowest-Id row in the group.
                var canonical = await _db.Errors.FirstOrDefaultAsync(
                    e => e.ApplicationId == appId
                      && e.EnvironmentId == envId
                      && e.Fingerprint == fingerprint
                      && !batchIds.Contains(e.Id),
                    ct);

                IEnumerable<Domain.Telemetry.ErrorRecord> mergeSources;
                if (canonical is null)
                {
                    canonical = rows[0];
                    canonical.Fingerprint = fingerprint;
                    canonical.FingerprintVersion = target;
                    updated++;
                    mergeSources = rows.Skip(1);
                }
                else
                {
                    mergeSources = rows;
                }

                foreach (var src in mergeSources)
                {
                    canonical.OccurrenceCount += src.OccurrenceCount;
                    if (src.FirstSeenAt < canonical.FirstSeenAt) canonical.FirstSeenAt = src.FirstSeenAt;
                    if (src.LastSeenAt > canonical.LastSeenAt)
                    {
                        canonical.LastSeenAt = src.LastSeenAt;
                        canonical.LastCorrelationId = src.LastCorrelationId;
                        if (!string.IsNullOrEmpty(src.ReleaseSha)) canonical.ReleaseSha = src.ReleaseSha;
                    }
                    canonical.FingerprintVersion = target;
                    _db.Errors.Remove(src);
                    merged++;
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        return new BatchOutcome(batch.Count, updated, merged);
    }

    private readonly record struct BatchOutcome(int Scanned, int Updated, int Merged);
}
