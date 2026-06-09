using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Observability.Domain.Telemetry;
using Observability.Infrastructure.Persistence;

namespace Observability.Api.Endpoints;

/// <summary>
/// Phase 3 dashboard read endpoints. Auth is intentionally a TODO until Phase 8 (RBAC); the
/// dashboard runs unauthenticated against an internal-only network for the MVP.
/// </summary>
public static class DashboardEndpoints
{
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        // App + environment metadata for filter dropdowns.
        app.MapGet("/api/apps", GetApps);

        var dash = app.MapGroup("/api/dashboard");
        dash.MapGet("/health", GetHealth);
        dash.MapGet("/errors", GetErrors);
        dash.MapGet("/background-jobs", GetBackgroundJobs);
        dash.MapGet("/events", GetEvents);
        dash.MapGet("/sessions", GetSessions);
    }

    /// <summary>
    /// Issue 10.2 — the PHI allowlist canary writes to a dedicated <c>canary-test</c> app. Reading
    /// this id lets the dashboard namespace that app out so its rows never pollute a real tenant's
    /// (e.g. SCH's) view. Empty/unset config means no canary app to hide.
    /// </summary>
    private static Guid? CanaryAppId(IConfiguration config) =>
        Guid.TryParse(config["Observability:CanaryApplicationId"], out var id) ? id : null;

    private static async Task<IResult> GetApps(ObservabilityDbContext db, IConfiguration config, CancellationToken ct)
    {
        var canaryAppId = CanaryAppId(config);
        var apps = await db.Applications
            .AsNoTracking()
            .Where(a => a.IsActive)
            .Where(a => canaryAppId == null || a.Id != canaryAppId)
            .OrderBy(a => a.Name)
            .Select(a => new
            {
                id = a.Id,
                slug = a.Slug,
                name = a.Name,
                description = a.Description,
                environments = a.Environments
                    .Where(e => e.IsActive)
                    .OrderBy(e => e.EnvironmentName)
                    .Select(e => new { id = e.Id, name = e.EnvironmentName })
                    .ToList()
            })
            .ToListAsync(ct);

        return Results.Ok(apps);
    }

    private static async Task<IResult> GetHealth(
        [FromQuery(Name = "app")] Guid? appId,
        [FromQuery(Name = "env")] Guid? envId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        ObservabilityDbContext db,
        CancellationToken ct)
    {
        if (appId is null || envId is null)
            return Results.BadRequest(new { error = "missing_filter", reason = "app and env are required." });

        var range = ResolveRange(from, to);

        var events = db.Events.AsNoTracking()
            .Where(e => e.ApplicationId == appId && e.EnvironmentId == envId
                        && e.CreatedAt >= range.From && e.CreatedAt < range.To);
        var errors = db.Errors.AsNoTracking()
            .Where(e => e.ApplicationId == appId && e.EnvironmentId == envId
                        && e.LastSeenAt >= range.From && e.LastSeenAt < range.To);

        var byEvent = await events
            .GroupBy(e => e.EventName)
            .Select(g => new { name = g.Key, count = g.LongCount() })
            .ToListAsync(ct);

        long Count(string name) => byEvent.FirstOrDefault(x => x.name == name)?.count ?? 0L;

        var pageViewsByFeature = await events
            .Where(e => e.EventName == "page_viewed" && e.FeatureArea != null)
            .GroupBy(e => e.FeatureArea!)
            .Select(g => new { feature = g.Key, count = g.LongCount() })
            .OrderByDescending(x => x.count)
            .Take(10)
            .ToListAsync(ct);

        var topFailingEndpoints = await errors
            .Where(e => e.EndpointGroup != null)
            .GroupBy(e => e.EndpointGroup!)
            .Select(g => new { endpoint_group = g.Key, occurrences = g.Sum(x => x.OccurrenceCount) })
            .OrderByDescending(x => x.occurrences)
            .Take(10)
            .ToListAsync(ct);

        var errorsByRelease = await errors
            .GroupBy(e => e.ReleaseSha ?? "unknown")
            .Select(g => new { release = g.Key, occurrences = g.Sum(x => x.OccurrenceCount) })
            .OrderByDescending(x => x.occurrences)
            .Take(10)
            .ToListAsync(ct);

        // Hourly sparkline buckets — small, zero-padded on the client.
        // EF Core translates DateTime.{Year,Month,Day,Hour} to DATEPART on SQL Server; we re-assemble
        // the bucket timestamp on the .NET side.
        var sparklineRaw = await events
            .GroupBy(e => new { e.CreatedAt.Year, e.CreatedAt.Month, e.CreatedAt.Day, e.CreatedAt.Hour, e.EventName })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, name = g.Key.EventName, count = g.LongCount() })
            .ToListAsync(ct);

        var sparklines = sparklineRaw
            .GroupBy(r => r.name)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new
                      {
                          t = new DateTime(r.Year, r.Month, r.Day, r.Hour, 0, 0, DateTimeKind.Utc),
                          c = r.count
                      })
                      .OrderBy(x => x.t)
                      .ToArray());

        return Results.Ok(new
        {
            range = new { from = range.From, to = range.To },
            cards = new
            {
                backend_500s = Count("server_error_occurred"),
                frontend_exceptions = Count("frontend_exception"),
                api_request_failures = Count("api_request_failed"),
                background_job_failures = Count("background_job_failed"),
                page_views = Count("page_viewed"),
                logins = Count("auth_login_success"),
            },
            by_event = byEvent,
            page_views_by_feature = pageViewsByFeature,
            top_failing_endpoint_groups = topFailingEndpoints,
            errors_by_release = errorsByRelease,
            sparklines
        });
    }

    private static async Task<IResult> GetErrors(
        [FromQuery(Name = "app")] Guid? appId,
        [FromQuery(Name = "env")] Guid? envId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? sort,
        [FromQuery] string? category,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ObservabilityDbContext db,
        CancellationToken ct)
    {
        if (appId is null || envId is null)
            return Results.BadRequest(new { error = "missing_filter", reason = "app and env are required." });

        var range = ResolveRange(from, to);
        var (skip, take) = ResolvePaging(page, pageSize);

        var query = db.Errors.AsNoTracking()
            .Where(e => e.ApplicationId == appId && e.EnvironmentId == envId
                        && e.LastSeenAt >= range.From && e.LastSeenAt < range.To);

        // Category is derived from which fields are populated, matching the ingestion classifier:
        // exception_type => backend server error; else job_name => background job; else frontend.
        query = category switch
        {
            "server" => query.Where(e => e.ExceptionType != null),
            "background_job" => query.Where(e => e.ExceptionType == null && e.JobName != null),
            "frontend" => query.Where(e => e.ExceptionType == null && e.JobName == null),
            _ => query,
        };

        query = sort switch
        {
            "occurrence_count" => query.OrderByDescending(e => e.OccurrenceCount),
            _ => query.OrderByDescending(e => e.LastSeenAt)
        };

        var total = await query.LongCountAsync(ct);
        var rows = await query.Skip(skip).Take(take)
            .Select(e => new
            {
                id = e.Id,
                fingerprint = e.Fingerprint,
                error_type = e.ErrorType,
                exception_type = e.ExceptionType,
                endpoint_group = e.EndpointGroup,
                job_name = e.JobName,
                normalized_route = e.NormalizedRoute,
                http_status_code = e.HttpStatusCode,
                release_sha = e.ReleaseSha,
                occurrence_count = e.OccurrenceCount,
                first_seen_at = e.FirstSeenAt,
                last_seen_at = e.LastSeenAt,
                last_correlation_id = e.LastCorrelationId
            })
            .ToListAsync(ct);

        return Results.Ok(new { total, page = skip / take, page_size = take, rows });
    }

    /// <summary>
    /// Issue 8.2 — background-job incident view from the <c>BackgroundJobFailures</c> sidecar. Unlike
    /// <see cref="GetErrors"/> (which derives a BG category from the global Errors table), this exposes
    /// the per-(JobName, Fingerprint) incident with its dedup metrics: total occurrences and how many
    /// were suppressed inside the alert window. Ordered by most recently seen.
    /// </summary>
    private static async Task<IResult> GetBackgroundJobs(
        [FromQuery(Name = "app")] Guid? appId,
        [FromQuery(Name = "env")] Guid? envId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ObservabilityDbContext db,
        CancellationToken ct)
    {
        if (appId is null || envId is null)
            return Results.BadRequest(new { error = "missing_filter", reason = "app and env are required." });

        var range = ResolveRange(from, to);
        var (skip, take) = ResolvePaging(page, pageSize);

        var query = db.BackgroundJobFailures.AsNoTracking()
            .Where(b => b.ApplicationId == appId && b.EnvironmentId == envId
                        && b.LastSeenAt >= range.From && b.LastSeenAt < range.To)
            .OrderByDescending(b => b.LastSeenAt);

        var total = await query.LongCountAsync(ct);
        var rows = await query.Skip(skip).Take(take)
            .Select(b => new
            {
                id = b.Id,
                job_name = b.JobName,
                error_type = b.ErrorType,
                fingerprint = b.Fingerprint,
                release_sha = b.ReleaseSha,
                occurrence_count = b.OccurrenceCount,
                suppressed_count = b.SuppressedCount,
                first_seen_at = b.FirstSeenAt,
                last_seen_at = b.LastSeenAt,
                last_suppressed_at = b.LastSuppressedAt,
            })
            .ToListAsync(ct);

        return Results.Ok(new { total, page = skip / take, page_size = take, rows });
    }

    private static async Task<IResult> GetEvents(
        [FromQuery(Name = "app")] Guid? appId,
        [FromQuery(Name = "env")] Guid? envId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery(Name = "event_name")] string? eventName,
        [FromQuery(Name = "distinct_id")] string? distinctId,
        [FromQuery(Name = "correlation_id")] string? correlationId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ObservabilityDbContext db,
        CancellationToken ct)
    {
        if (appId is null || envId is null)
            return Results.BadRequest(new { error = "missing_filter", reason = "app and env are required." });

        var range = ResolveRange(from, to);
        var (skip, take) = ResolvePaging(page, pageSize);

        var q = db.Events.AsNoTracking()
            .Where(e => e.ApplicationId == appId && e.EnvironmentId == envId
                        && e.CreatedAt >= range.From && e.CreatedAt < range.To);

        if (!string.IsNullOrWhiteSpace(eventName)) q = q.Where(e => e.EventName == eventName);
        if (!string.IsNullOrWhiteSpace(distinctId)) q = q.Where(e => e.DistinctId == distinctId);
        if (!string.IsNullOrWhiteSpace(correlationId)) q = q.Where(e => e.CorrelationId == correlationId);

        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(e => e.CreatedAt)
            .Skip(skip).Take(take)
            .Select(e => new
            {
                id = e.Id,
                event_name = e.EventName,
                distinct_id = e.DistinctId,
                session_id = e.SessionId,
                correlation_id = e.CorrelationId,
                normalized_route = e.NormalizedRoute,
                endpoint_group = e.EndpointGroup,
                feature_area = e.FeatureArea,
                release_sha = e.ReleaseSha,
                occurred_at = e.OccurredAt,
                created_at = e.CreatedAt,
                properties_json = e.PropertiesJson
            })
            .ToListAsync(ct);

        return Results.Ok(new { total, page = skip / take, page_size = take, rows });
    }

    /// <summary>
    /// Phase 5: list sessions filtered by app + env + time range. Ordered by LastSeenAt desc.
    /// </summary>
    private static async Task<IResult> GetSessions(
        [FromQuery(Name = "app")] Guid? appId,
        [FromQuery(Name = "env")] Guid? envId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery(Name = "errors_only")] bool? errorsOnly,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ObservabilityDbContext db,
        CancellationToken ct)
    {
        if (appId is null || envId is null)
            return Results.BadRequest(new { error = "missing_filter", reason = "app and env are required." });

        var range = ResolveRange(from, to);
        var (skip, take) = ResolvePaging(page, pageSize);

        var q = db.Sessions.AsNoTracking()
            .Where(s => s.ApplicationId == appId && s.EnvironmentId == envId
                     && s.LastSeenAt >= range.From && s.LastSeenAt < range.To);
        if (errorsOnly == true) q = q.Where(s => s.HasError);

        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(s => s.LastSeenAt)
            .Skip(skip).Take(take)
            .Select(s => new
            {
                id = s.Id,
                session_id = s.SessionId,
                distinct_id = s.DistinctId,
                started_at = s.StartedAt,
                ended_at = s.EndedAt,
                last_seen_at = s.LastSeenAt,
                has_error = s.HasError,
                release_sha = s.ReleaseSha,
            })
            .ToListAsync(ct);

        return Results.Ok(new { total, page = skip / take, page_size = take, rows });
    }

    private static (DateTime From, DateTime To) ResolveRange(DateTime? from, DateTime? to)
    {
        var resolvedTo = (to ?? DateTime.UtcNow);
        var resolvedFrom = from ?? resolvedTo.AddHours(-24);
        if (resolvedFrom >= resolvedTo) resolvedFrom = resolvedTo.AddHours(-24);
        return (DateTime.SpecifyKind(resolvedFrom, DateTimeKind.Utc), DateTime.SpecifyKind(resolvedTo, DateTimeKind.Utc));
    }

    private static (int Skip, int Take) ResolvePaging(int? page, int? pageSize)
    {
        var take = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var p = Math.Max(page ?? 0, 0);
        return (p * take, take);
    }
}
