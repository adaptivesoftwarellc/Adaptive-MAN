using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Observability.Api.Middleware;
using Observability.Domain.Audit;
using Observability.Infrastructure.Persistence;

namespace Observability.Api.Endpoints;

/// <summary>
/// Issue 10.5 bulk data export. Three streaming NDJSON endpoints under <c>/api/admin/export/*</c>
/// that let operators extract events / errors / safety violations without hand-written SQL —
/// backing the "we own our data" promise over PostHog. Rows stream via
/// <c>IAsyncEnumerable&lt;T&gt;</c> (no buffering), each request is capped to a 90-day window, and a
/// single audit row is written <b>after</b> the stream finishes so partial failures stay visible.
/// Same admin-key gate as <see cref="AdminEndpoints"/>.
/// </summary>
public static class ExportEndpoints
{
    private const string ActorType = "admin_key";
    private const string NdjsonContentType = "application/x-ndjson";
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(90);
    private static readonly byte[] Newline = "\n"u8.ToArray();

    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var export = app.MapGroup("/api/admin/export").AddAdminKeyAuth();
        export.MapGet("/events", ExportEvents);
        export.MapGet("/errors", ExportErrors);
        export.MapGet("/safety-violations", ExportSafetyViolations);
    }

    private static async Task<IResult> ExportEvents(
        [FromQuery(Name = "app")] Guid? appId,
        [FromQuery(Name = "env")] Guid? envId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery(Name = "event_name")] string? eventName,
        [FromQuery(Name = "distinct_id")] string? distinctId,
        [FromQuery(Name = "correlation_id")] string? correlationId,
        [FromQuery] string? format,
        ObservabilityDbContext db,
        HttpContext http,
        IOptions<JsonOptions> json,
        CancellationToken ct)
    {
        var failure = Validate(appId, format, from, to, out var range);
        if (failure is not null) return failure;

        var q = db.Events.AsNoTracking()
            .Where(e => e.ApplicationId == appId && e.CreatedAt >= range.From && e.CreatedAt < range.To);
        if (envId is not null) q = q.Where(e => e.EnvironmentId == envId);
        if (!string.IsNullOrWhiteSpace(eventName)) q = q.Where(e => e.EventName == eventName);
        if (!string.IsNullOrWhiteSpace(distinctId)) q = q.Where(e => e.DistinctId == distinctId);
        if (!string.IsNullOrWhiteSpace(correlationId)) q = q.Where(e => e.CorrelationId == correlationId);

        var rows = q.OrderBy(e => e.Id).Select(e => new
        {
            id = e.Id,
            application_id = e.ApplicationId,
            environment_id = e.EnvironmentId,
            event_name = e.EventName,
            distinct_id = e.DistinctId,
            session_id = e.SessionId,
            correlation_id = e.CorrelationId,
            normalized_route = e.NormalizedRoute,
            endpoint_group = e.EndpointGroup,
            feature_area = e.FeatureArea,
            properties_json = e.PropertiesJson,
            release_sha = e.ReleaseSha,
            occurred_at = e.OccurredAt,
            created_at = e.CreatedAt,
        }).AsAsyncEnumerable();

        var filters = new { event_name = eventName, distinct_id = distinctId, correlation_id = correlationId };
        await StreamAsync(http, db, "admin.export.events", appId!.Value, envId, range, filters,
            rows, json.Value.SerializerOptions, ct);
        return Results.Empty;
    }

    private static async Task<IResult> ExportErrors(
        [FromQuery(Name = "app")] Guid? appId,
        [FromQuery(Name = "env")] Guid? envId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? format,
        ObservabilityDbContext db,
        HttpContext http,
        IOptions<JsonOptions> json,
        CancellationToken ct)
    {
        var failure = Validate(appId, format, from, to, out var range);
        if (failure is not null) return failure;

        var q = db.Errors.AsNoTracking()
            .Where(e => e.ApplicationId == appId && e.LastSeenAt >= range.From && e.LastSeenAt < range.To);
        if (envId is not null) q = q.Where(e => e.EnvironmentId == envId);

        var rows = q.OrderBy(e => e.Id).Select(e => new
        {
            id = e.Id,
            application_id = e.ApplicationId,
            environment_id = e.EnvironmentId,
            fingerprint = e.Fingerprint,
            fingerprint_version = e.FingerprintVersion,
            error_type = e.ErrorType,
            exception_type = e.ExceptionType,
            endpoint_group = e.EndpointGroup,
            job_name = e.JobName,
            normalized_route = e.NormalizedRoute,
            http_status_code = e.HttpStatusCode,
            release_sha = e.ReleaseSha,
            properties_json = e.PropertiesJson,
            occurrence_count = e.OccurrenceCount,
            first_seen_at = e.FirstSeenAt,
            last_seen_at = e.LastSeenAt,
            last_correlation_id = e.LastCorrelationId,
        }).AsAsyncEnumerable();

        await StreamAsync(http, db, "admin.export.errors", appId!.Value, envId, range, new { },
            rows, json.Value.SerializerOptions, ct);
        return Results.Empty;
    }

    private static async Task<IResult> ExportSafetyViolations(
        [FromQuery(Name = "app")] Guid? appId,
        [FromQuery(Name = "env")] Guid? envId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? format,
        ObservabilityDbContext db,
        HttpContext http,
        IOptions<JsonOptions> json,
        CancellationToken ct)
    {
        var failure = Validate(appId, format, from, to, out var range);
        if (failure is not null) return failure;

        var q = db.SafetyViolations.AsNoTracking()
            .Where(v => v.ApplicationId == appId && v.CreatedAt >= range.From && v.CreatedAt < range.To);
        if (envId is not null) q = q.Where(v => v.EnvironmentId == envId);

        var rows = q.OrderBy(v => v.Id).Select(v => new
        {
            id = v.Id,
            application_id = v.ApplicationId,
            environment_id = v.EnvironmentId,
            event_name = v.EventName,
            rejected_field = v.RejectedField,
            reason = v.Reason,
            created_at = v.CreatedAt,
        }).AsAsyncEnumerable();

        await StreamAsync(http, db, "admin.export.safety_violations", appId!.Value, envId, range, new { },
            rows, json.Value.SerializerOptions, ct);
        return Results.Empty;
    }

    /// <summary>
    /// Validates the shared export contract and resolves the time range. Returns a 400
    /// <see cref="IResult"/> on failure, or <c>null</c> if the request is good to stream. Runs
    /// before any bytes are written so error bodies are clean JSON, not a broken NDJSON stream.
    /// </summary>
    private static IResult? Validate(Guid? appId, string? format, DateTime? from, DateTime? to,
        out (DateTime From, DateTime To) range)
    {
        range = ResolveRange(from, to);

        if (appId is null)
            return Results.BadRequest(new { error = "missing_filter", reason = "app is required for exports." });

        if (!string.IsNullOrWhiteSpace(format) && !string.Equals(format, "ndjson", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "unsupported_format", reason = "only 'ndjson' is supported." });

        if (range.To - range.From > MaxRange)
            return Results.BadRequest(new
            {
                error = "range_too_large",
                reason = "export range must be <= 90 days. Chunk the request into <=90-day windows.",
            });

        return null;
    }

    /// <summary>
    /// Streams <paramref name="rows"/> as NDJSON straight to the response body, then writes one
    /// audit row in a <c>finally</c> so it lands even on partial failure or client disconnect. The
    /// audit write uses <see cref="CancellationToken.None"/> so a canceled request can't also drop
    /// the audit record.
    /// </summary>
    private static async Task StreamAsync<T>(
        HttpContext http,
        ObservabilityDbContext db,
        string action,
        Guid appId,
        Guid? envId,
        (DateTime From, DateTime To) range,
        object filters,
        IAsyncEnumerable<T> rows,
        JsonSerializerOptions json,
        CancellationToken ct)
    {
        http.Response.ContentType = NdjsonContentType;

        var count = 0L;
        var status = "completed";
        string? error = null;
        try
        {
            await foreach (var row in rows.WithCancellation(ct))
            {
                await JsonSerializer.SerializeAsync(http.Response.Body, row, json, ct);
                await http.Response.Body.WriteAsync(Newline, ct);
                count++;
            }
            await http.Response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException)
        {
            status = "canceled";
            throw;
        }
        catch (Exception ex)
        {
            status = "failed";
            error = ex.GetType().Name;
            throw;
        }
        finally
        {
            await WriteAuditAsync(db, action, appId, envId, http, new
            {
                count,
                from = range.From,
                to = range.To,
                status,
                error,
                filters,
            });
        }
    }

    private static async Task WriteAuditAsync(
        ObservabilityDbContext db,
        string action,
        Guid? appId,
        Guid? envId,
        HttpContext http,
        object details)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = action,
                ActorType = ActorType,
                ApplicationId = appId,
                EnvironmentId = envId,
                CorrelationId = http.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString(),
                DetailsJson = JsonSerializer.Serialize(details),
            });
            // None, not the request token: a client disconnect must not also drop the audit row.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // Best-effort audit: a failed export should surface as the broken stream, not be masked
            // by a secondary failure writing its own audit trail.
        }
    }

    private static (DateTime From, DateTime To) ResolveRange(DateTime? from, DateTime? to)
    {
        var resolvedTo = to ?? DateTime.UtcNow;
        var resolvedFrom = from ?? resolvedTo.AddHours(-24);
        if (resolvedFrom >= resolvedTo) resolvedFrom = resolvedTo.AddHours(-24);
        return (DateTime.SpecifyKind(resolvedFrom, DateTimeKind.Utc), DateTime.SpecifyKind(resolvedTo, DateTimeKind.Utc));
    }
}
