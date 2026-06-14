using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Observability.Application.Retention;
using Observability.Domain.Audit;

namespace Observability.Infrastructure.Persistence;

public sealed class RetentionSweeper : IRetentionSweeper
{
    private readonly ObservabilityDbContext _db;
    private readonly RetentionOptions _options;

    public RetentionSweeper(ObservabilityDbContext db, IOptions<RetentionOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<DateTime?> GetLastSweepAtUtcAsync(CancellationToken ct)
    {
        var last = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.Action == "admin.retention.swept")
            .OrderByDescending(a => a.OccurredAt)
            .Select(a => (DateTime?)a.OccurredAt)
            .FirstOrDefaultAsync(ct);
        return last;
    }

    public async Task<RetentionSweepResult> SweepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var batchSize = _options.DeleteBatchSize > 0 ? _options.DeleteBatchSize : 1000;

        // Snapshot the per-environment settings up front; the deletes below don't touch this table.
        var environments = await _db.AppEnvironments
            .AsNoTracking()
            .Select(e => new { e.ApplicationId, e.Id, e.EventRetentionDays, e.ErrorRetentionDays })
            .ToListAsync(ct);

        long eventsDeleted = 0, errorsDeleted = 0;

        foreach (var env in environments)
        {
            var eventCutoff = now.AddDays(-(env.EventRetentionDays ?? _options.EventRetentionDays));
            var errorCutoff = now.AddDays(-(env.ErrorRetentionDays ?? _options.ErrorRetentionDays));

            eventsDeleted += await DeleteBatchedAsync(
                _db.Events.Where(x =>
                    x.ApplicationId == env.ApplicationId && x.EnvironmentId == env.Id && x.CreatedAt < eventCutoff),
                batchSize, ct);

            errorsDeleted += await DeleteBatchedAsync(
                _db.Errors.Where(x =>
                    x.ApplicationId == env.ApplicationId && x.EnvironmentId == env.Id && x.LastSeenAt < errorCutoff),
                batchSize, ct);
        }

        // Audit log retention is global, not per-environment (enforces the 8.7/PR C 365-day policy).
        // Prune by age only: this run's own admin.retention.swept row is added afterward with
        // OccurredAt = now, so it can never fall before the cutoff. Filtering on Action instead would
        // permanently exempt every past sweep row and let them grow unbounded.
        var auditCutoff = now.AddDays(-_options.AuditLogRetentionDays);
        var auditDeleted = await DeleteBatchedAsync(
            _db.AuditLogs.Where(a => a.OccurredAt < auditCutoff),
            batchSize, ct);

        var result = new RetentionSweepResult(eventsDeleted, errorsDeleted, auditDeleted, environments.Count);

        _db.AuditLogs.Add(new AuditLog
        {
            OccurredAt = now,
            Action = "admin.retention.swept",
            ActorType = "system",
            DetailsJson = JsonSerializer.Serialize(new
            {
                events_deleted = result.EventsDeleted,
                errors_deleted = result.ErrorsDeleted,
                audit_logs_deleted = result.AuditLogsDeleted,
                environments_swept = result.EnvironmentsSwept,
                ran_at = now,
            }),
        });
        await _db.SaveChangesAsync(ct);

        return result;
    }

    /// <summary>
    /// Deletes in capped batches so a large backlog doesn't build one giant transaction. Uses
    /// load+RemoveRange rather than ExecuteDelete because the latter isn't supported by the InMemory
    /// provider the integration tests run on, and the volumes here are a nightly trickle, not a bulk job.
    /// </summary>
    private async Task<long> DeleteBatchedAsync<T>(IQueryable<T> filtered, int batchSize, CancellationToken ct)
        where T : class
    {
        long total = 0;
        while (true)
        {
            var batch = await filtered.Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            _db.Set<T>().RemoveRange(batch);
            await _db.SaveChangesAsync(ct);
            total += batch.Count;
            if (batch.Count < batchSize) break;
        }
        return total;
    }
}
