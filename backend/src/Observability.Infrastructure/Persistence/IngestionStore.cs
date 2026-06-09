using Microsoft.EntityFrameworkCore;
using Observability.Application.Ingestion;
using Observability.Domain.Telemetry;

namespace Observability.Infrastructure.Persistence;

public sealed class IngestionStore : IIngestionStore
{
    private readonly ObservabilityDbContext _db;

    public IngestionStore(ObservabilityDbContext db) => _db = db;

    public async Task AddEventAsync(EventRecord record, CancellationToken ct)
    {
        _db.Events.Add(record);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpsertErrorAsync(ErrorRecord record, CancellationToken ct)
    {
        var existing = await _db.Errors
            .FirstOrDefaultAsync(
                e => e.ApplicationId == record.ApplicationId
                  && e.EnvironmentId == record.EnvironmentId
                  && e.Fingerprint == record.Fingerprint,
                ct);

        if (existing is null)
        {
            _db.Errors.Add(record);
        }
        else
        {
            existing.OccurrenceCount++;
            existing.LastSeenAt = record.LastSeenAt;
            existing.LastCorrelationId = record.LastCorrelationId;
            if (!string.IsNullOrEmpty(record.ReleaseSha))
            {
                existing.ReleaseSha = record.ReleaseSha;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task AddSafetyViolationAsync(SafetyViolation violation, CancellationToken ct)
    {
        _db.SafetyViolations.Add(violation);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpsertBackgroundJobFailureAsync(BackgroundJobFailure failure, TimeSpan dedupWindow, CancellationToken ct)
    {
        var existing = await _db.BackgroundJobFailures
            .FirstOrDefaultAsync(
                f => f.ApplicationId == failure.ApplicationId
                  && f.EnvironmentId == failure.EnvironmentId
                  && f.Fingerprint == failure.Fingerprint,
                ct);

        if (existing is null)
        {
            _db.BackgroundJobFailures.Add(failure);
        }
        else
        {
            existing.OccurrenceCount++;
            // Within the dedup window, the duplicate is "suppressed" from the alerting POV but
            // still counted. The window is the alert dampener, not a counter dampener.
            if (failure.LastSeenAt - existing.LastSeenAt < dedupWindow)
            {
                existing.LastSuppressedAt = failure.LastSeenAt;
                existing.SuppressedCount++;
            }
            existing.LastSeenAt = failure.LastSeenAt;
            if (!string.IsNullOrEmpty(failure.ReleaseSha))
            {
                existing.ReleaseSha = failure.ReleaseSha;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<TimeSpan> ResolveBackgroundJobDedupWindowAsync(Guid applicationId, Guid environmentId, TimeSpan defaultWindow, CancellationToken ct)
    {
        var overrideMinutes = await _db.AppEnvironments
            .AsNoTracking()
            .Where(e => e.Id == environmentId && e.ApplicationId == applicationId)
            .Select(e => e.BackgroundJobDedupWindowMinutes)
            .FirstOrDefaultAsync(ct);

        return overrideMinutes is > 0 ? TimeSpan.FromMinutes(overrideMinutes.Value) : defaultWindow;
    }

    public async Task UpsertSessionStartAsync(Session session, CancellationToken ct)
    {
        var existing = await _db.Sessions.FirstOrDefaultAsync(
            s => s.ApplicationId == session.ApplicationId
              && s.EnvironmentId == session.EnvironmentId
              && s.SessionId == session.SessionId, ct);
        if (existing is null)
        {
            _db.Sessions.Add(session);
        }
        else
        {
            // Idempotent re-start: take the earliest StartedAt and update DistinctId / release_sha if newer.
            if (session.StartedAt < existing.StartedAt) existing.StartedAt = session.StartedAt;
            if (!string.IsNullOrEmpty(session.DistinctId)) existing.DistinctId = session.DistinctId;
            if (session.LastSeenAt > existing.LastSeenAt) existing.LastSeenAt = session.LastSeenAt;
            if (!string.IsNullOrEmpty(session.ReleaseSha)) existing.ReleaseSha = session.ReleaseSha;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpsertSessionEndAsync(Guid applicationId, Guid environmentId, string sessionId, DateTime endedAt, CancellationToken ct)
    {
        var existing = await _db.Sessions.FirstOrDefaultAsync(
            s => s.ApplicationId == applicationId
              && s.EnvironmentId == environmentId
              && s.SessionId == sessionId, ct);
        if (existing is null)
        {
            // Orphan /end (no prior /start): drop silently. Inserting a closing-only row with
            // empty DistinctId pollutes the dashboard list with malformed sessions, and there's
            // no bracket to preserve since /start never arrived. The endpoint still returns 202
            // for idempotency.
            return;
        }
        existing.EndedAt = endedAt;
        if (endedAt > existing.LastSeenAt) existing.LastSeenAt = endedAt;
        await _db.SaveChangesAsync(ct);
    }

    public async Task BumpSessionAsync(Guid applicationId, Guid environmentId, string sessionId, DateTime occurredAt, bool isError, CancellationToken ct)
    {
        var existing = await _db.Sessions.FirstOrDefaultAsync(
            s => s.ApplicationId == applicationId
              && s.EnvironmentId == environmentId
              && s.SessionId == sessionId, ct);
        if (existing is null) return; // session was never started; we don't fabricate one from event traffic
        if (occurredAt > existing.LastSeenAt) existing.LastSeenAt = occurredAt;
        if (isError) existing.HasError = true;
        await _db.SaveChangesAsync(ct);
    }
}
