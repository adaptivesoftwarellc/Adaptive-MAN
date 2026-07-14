using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Observability.Api.Middleware;
using Observability.Application.Ingestion;
using Observability.Domain.Alerting;
using Observability.Domain.Telemetry;
using Observability.Infrastructure.Persistence;

namespace Observability.Api.Endpoints;

/// <summary>
/// Phase 3 dashboard read endpoints. As of Issue 8.6 RBAC every read requires an authenticated user
/// (<c>AddRequireUser</c>). Reads are app-scoped: global-read roles (Admin/Developer/Viewer) see any
/// app; AppOwner is limited to assigned apps (a cross-app <c>?app=</c> is 403). Admin/Developer reads
/// are audited.
/// </summary>
public static class DashboardEndpoints
{
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        // Empty-prefix group so /api/apps and /api/dashboard/* share the one auth gate.
        var secured = app.MapGroup("").AddRequireUser();

        // App + environment metadata for filter dropdowns (filtered to the caller's readable apps).
        secured.MapGet("/api/apps", GetApps);

        var dash = secured.MapGroup("/api/dashboard");
        dash.AddEndpointFilter(EnforceAppScopeAsync);
        dash.AddEndpointFilter(AuditPrivilegedAccessAsync);
        dash.MapGet("/health", GetHealth);
        dash.MapGet("/errors", GetErrors);
        dash.MapGet("/background-jobs", GetBackgroundJobs);
        dash.MapGet("/events", GetEvents);
        dash.MapGet("/sessions", GetSessions);
        dash.MapGet("/alerts", GetAlerts);
        dash.MapGet("/insights/trends", GetTrends);
        dash.MapGet("/annotations", GetAnnotations);
    }

    /// <summary>
    /// Tenant isolation for the read path (Issue 8.6). The <c>?app=</c> param drives every dashboard
    /// query, so checking it once here covers all data endpoints uniformly: an AppOwner asking for an
    /// app it doesn't own gets 403 before any query runs. Global readers always pass.
    /// </summary>
    private static async ValueTask<object?> EnforceAppScopeAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var user = http.GetUser();
        if (Guid.TryParse(http.Request.Query["app"], out var appId) && !user.CanReadApplication(appId))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        return await next(ctx);
    }

    /// <summary>Issue 8.6 acceptance — Admin/Developer access is logged. Records the app/env viewed.</summary>
    private static async ValueTask<object?> AuditPrivilegedAccessAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var result = await next(ctx);

        var http = ctx.HttpContext;
        var user = http.GetUserOrNull();
        if (user is { IsPrivileged: true } && Guid.TryParse(http.Request.Query["app"], out var appId))
        {
            var db = http.RequestServices.GetRequiredService<ObservabilityDbContext>();
            Guid.TryParse(http.Request.Query["env"], out var envId);
            AuditWriter.Add(db, http, "access.dashboard", appId, envId == Guid.Empty ? null : envId, new
            {
                email = user.Email,
                role = user.Role.ToString(),
                path = http.Request.Path.Value,
            });
            await db.SaveChangesAsync(http.RequestAborted);
        }

        return result;
    }

    /// <summary>
    /// Issue 10.2 — the PHI allowlist canary writes to a dedicated <c>canary-test</c> app. Reading
    /// this id lets the dashboard namespace that app out so its rows never pollute a real tenant's
    /// (e.g. WMS's) view. Empty/unset config means no canary app to hide.
    /// </summary>
    private static Guid? CanaryAppId(IConfiguration config) =>
        Guid.TryParse(config["Observability:CanaryApplicationId"], out var id) ? id : null;

    private static async Task<IResult> GetApps(HttpContext http, ObservabilityDbContext db, IConfiguration config, CancellationToken ct)
    {
        var user = http.GetUser();
        var canaryAppId = CanaryAppId(config);
        var isGlobalReader = user.IsGlobalReader;
        var owned = user.OwnedApplicationIds.ToList();
        var apps = await db.Applications
            .AsNoTracking()
            .Where(a => a.IsActive)
            .Where(a => canaryAppId == null || a.Id != canaryAppId)
            // AppOwner sees only assigned apps; global-read roles see all.
            .Where(a => isGlobalReader || owned.Contains(a.Id))
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

        // Error-category cards come from the Errors table, not Events: server_error_occurred /
        // frontend_exception / background_job_failed are persisted as Errors (see IngestionService).
        // Sum OccurrenceCount for total occurrences, matching GetErrors' categorization and the
        // errors_by_release / top_failing_endpoint_groups breakdowns below.
        long errBackend500 = await errors.Where(e => e.ExceptionType != null)
            .SumAsync(e => (long?)e.OccurrenceCount, ct) ?? 0L;
        long errBackgroundJobs = await errors.Where(e => e.ExceptionType == null && e.JobName != null)
            .SumAsync(e => (long?)e.OccurrenceCount, ct) ?? 0L;
        long errFrontend = await errors.Where(e => e.ExceptionType == null && e.JobName == null)
            .SumAsync(e => (long?)e.OccurrenceCount, ct) ?? 0L;

        // Event-based cards over the window immediately preceding this one (equal length), so the UI
        // can show period-over-period deltas. Only the EVENT cards get previous values: the error
        // cards can't — Errors rows are deduplicated with a lifetime OccurrenceCount and a single
        // LastSeenAt, so an ongoing error attributes its whole count to whichever window holds
        // LastSeenAt and a two-window comparison would be structurally wrong.
        var prevFrom = range.From - (range.To - range.From);
        var prevByEvent = await db.Events.AsNoTracking()
            .Where(e => e.ApplicationId == appId && e.EnvironmentId == envId
                        && e.CreatedAt >= prevFrom && e.CreatedAt < range.From)
            .GroupBy(e => e.EventName)
            .Select(g => new { name = g.Key, count = g.LongCount() })
            .ToListAsync(ct);
        long PrevCount(string name) => prevByEvent.FirstOrDefault(x => x.name == name)?.count ?? 0L;

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
                backend_500s = errBackend500,
                frontend_exceptions = errFrontend,
                api_request_failures = Count("api_request_failed"),
                background_job_failures = errBackgroundJobs,
                page_views = Count("page_viewed"),
                logins = Count("auth_login_success"),
            },
            // Event-based cards only — see the prevByEvent comment for why error cards are absent.
            cards_previous = new
            {
                api_request_failures = PrevCount("api_request_failed"),
                page_views = PrevCount("page_viewed"),
                logins = PrevCount("auth_login_success"),
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

    /// <summary>
    /// Issue 8.3 — fired-alert feed. The alert engine (Worker) is visibility-only until 8.4
    /// notifications land, so this read is the only consumer of the <c>FiredAlerts</c> it persists.
    /// App-wide rules produce alerts with a null <c>EnvironmentId</c>; those surface under any env view
    /// alongside the selected env's own alerts. Ordered most-recently-fired first.
    /// </summary>
    private static async Task<IResult> GetAlerts(
        [FromQuery(Name = "app")] Guid? appId,
        [FromQuery(Name = "env")] Guid? envId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery(Name = "rule_type")] string? ruleType,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ObservabilityDbContext db,
        CancellationToken ct)
    {
        if (appId is null || envId is null)
            return Results.BadRequest(new { error = "missing_filter", reason = "app and env are required." });

        var range = ResolveRange(from, to);
        var (skip, take) = ResolvePaging(page, pageSize);

        var query =
            from f in db.FiredAlerts.AsNoTracking()
            where f.ApplicationId == appId
                  && (f.EnvironmentId == envId || f.EnvironmentId == null)
                  && f.FiredAt >= range.From && f.FiredAt < range.To
            join r in db.AlertRules.AsNoTracking() on f.AlertRuleId equals r.Id into rj
            from r in rj.DefaultIfEmpty()
            orderby f.FiredAt descending
            select new { f, rule_name = r != null ? r.Name : null };

        if (Enum.TryParse<AlertRuleType>(ruleType, out var parsedRuleType))
            query = query.Where(x => x.f.RuleType == parsedRuleType);

        var total = await query.LongCountAsync(ct);
        var page0 = await query.Skip(skip).Take(take).ToListAsync(ct);

        // RuleType -> string after materialization: the enum has a value-converter to int, and
        // Enum.ToString() doesn't translate to SQL on the SqlServer provider.
        var rows = page0.Select(x => new
        {
            id = x.f.Id,
            alert_rule_id = x.f.AlertRuleId,
            rule_name = x.rule_name,
            rule_type = x.f.RuleType.ToString(),
            environment_id = x.f.EnvironmentId,
            fired_at = x.f.FiredAt,
            observed_value = x.f.ObservedValue,
            threshold = x.f.Threshold,
            summary = x.f.Summary,
            details_json = x.f.DetailsJson,
        });

        return Results.Ok(new { total, page = skip / take, page_size = take, rows });
    }

    // -----------------------------------------------------------------------
    // Insights — Phase A of docs/product-analytics-plan.md.
    // -----------------------------------------------------------------------

    /// <summary>Breakdown dimensions are typed columns only — never free-form JSON properties.</summary>
    private static readonly HashSet<string> TrendBreakdowns =
        new(StringComparer.Ordinal) { "feature_area", "release_sha", "endpoint_group" };

    private const int TrendMaxEvents = 5;
    private const int TrendMaxBreakdownValues = 10;

    /// <summary>
    /// Trends over the Events table: 1–5 catalog events, hour/day/week bucketing, optional typed-column
    /// breakdown, totals or unique users. SQL aggregates at hour (count) or target-bucket (unique_users)
    /// granularity via the same DATEPART grouping the health sparklines use; day/week buckets for
    /// <c>agg=count</c> are server-side rollups of the hour rows. Week supports <c>agg=count</c> only,
    /// and unique-user series totals come from a separate range-wide COUNT(DISTINCT) — per-bucket
    /// distinct counts never sum (a user active in N buckets is still one user).
    /// </summary>
    private static async Task<IResult> GetTrends(
        [FromQuery(Name = "app")] Guid? appId,
        [FromQuery(Name = "env")] Guid? envId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery(Name = "events")] string? eventsCsv,
        [FromQuery] string? interval,
        [FromQuery] string? breakdown,
        [FromQuery] string? agg,
        ObservabilityDbContext db,
        CancellationToken ct)
    {
        if (appId is null || envId is null)
            return Results.BadRequest(new { error = "missing_filter", reason = "app and env are required." });

        var names = (eventsCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (names.Length is 0 or > TrendMaxEvents)
            return Results.BadRequest(new { error = "invalid_events", reason = $"Provide 1–{TrendMaxEvents} comma-separated event names." });
        var unknown = names.Where(n => !EventCatalog.Phase1.ContainsKey(n)).ToArray();
        if (unknown.Length > 0)
            return Results.BadRequest(new { error = "unknown_event", reason = $"Not in the event catalog: {string.Join(", ", unknown)}." });

        if (breakdown is not null && !TrendBreakdowns.Contains(breakdown))
            return Results.BadRequest(new { error = "invalid_breakdown", reason = $"breakdown must be one of: {string.Join(", ", TrendBreakdowns)}." });

        var uniqueUsers = agg switch
        {
            null or "count" => false,
            "unique_users" => true,
            _ => (bool?)null,
        } ?? false;
        if (agg is not (null or "count" or "unique_users"))
            return Results.BadRequest(new { error = "invalid_agg", reason = "agg must be count or unique_users." });

        var range = ResolveRange(from, to);
        var span = range.To - range.From;
        var resolvedInterval = interval ?? (span <= TimeSpan.FromHours(48) ? "hour" : "day");
        if (resolvedInterval is not ("hour" or "day" or "week"))
            return Results.BadRequest(new { error = "invalid_interval", reason = "interval must be hour, day or week." });
        if (uniqueUsers && resolvedInterval == "week")
            return Results.BadRequest(new { error = "unsupported_combination", reason = "unique_users supports hour and day intervals only (distinct counts cannot be rolled up)." });

        // List<T>.Contains, not array Contains: with newer SDKs the array form binds to the
        // MemoryExtensions ReadOnlySpan overload, which EF cannot evaluate as a query parameter.
        var nameList = names.ToList();
        var query = db.Events.AsNoTracking()
            .Where(e => e.ApplicationId == appId && e.EnvironmentId == envId
                        && e.CreatedAt >= range.From && e.CreatedAt < range.To
                        && nameList.Contains(e.EventName));

        // Group at hour granularity for counts (rolled up server-side), or at target granularity for
        // unique users. Anonymous DATEPART keys translate on SQL Server and evaluate on InMemory.
        List<TrendRow> raw;
        var hourKeyed = !uniqueUsers || resolvedInterval == "hour";
        if (breakdown is null)
        {
            raw = hourKeyed
                ? await query
                    .GroupBy(e => new { e.CreatedAt.Year, e.CreatedAt.Month, e.CreatedAt.Day, e.CreatedAt.Hour, e.EventName })
                    .Select(g => new TrendRow(
                        g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, g.Key.EventName, null,
                        uniqueUsers ? g.Select(x => x.DistinctId).Distinct().LongCount() : g.LongCount()))
                    .ToListAsync(ct)
                : await query
                    .GroupBy(e => new { e.CreatedAt.Year, e.CreatedAt.Month, e.CreatedAt.Day, e.EventName })
                    .Select(g => new TrendRow(
                        g.Key.Year, g.Key.Month, g.Key.Day, 0, g.Key.EventName, null,
                        g.Select(x => x.DistinctId).Distinct().LongCount()))
                    .ToListAsync(ct);
        }
        else
        {
            // The breakdown column must appear inline in the key for translation, hence one branch per column.
            raw = breakdown switch
            {
                "feature_area" => hourKeyed
                    ? await query
                        .GroupBy(e => new { e.CreatedAt.Year, e.CreatedAt.Month, e.CreatedAt.Day, e.CreatedAt.Hour, e.EventName, Key = e.FeatureArea })
                        .Select(g => new TrendRow(
                            g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, g.Key.EventName, g.Key.Key,
                            uniqueUsers ? g.Select(x => x.DistinctId).Distinct().LongCount() : g.LongCount()))
                        .ToListAsync(ct)
                    : await query
                        .GroupBy(e => new { e.CreatedAt.Year, e.CreatedAt.Month, e.CreatedAt.Day, e.EventName, Key = e.FeatureArea })
                        .Select(g => new TrendRow(
                            g.Key.Year, g.Key.Month, g.Key.Day, 0, g.Key.EventName, g.Key.Key,
                            g.Select(x => x.DistinctId).Distinct().LongCount()))
                        .ToListAsync(ct),
                "release_sha" => hourKeyed
                    ? await query
                        .GroupBy(e => new { e.CreatedAt.Year, e.CreatedAt.Month, e.CreatedAt.Day, e.CreatedAt.Hour, e.EventName, Key = e.ReleaseSha })
                        .Select(g => new TrendRow(
                            g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, g.Key.EventName, g.Key.Key,
                            uniqueUsers ? g.Select(x => x.DistinctId).Distinct().LongCount() : g.LongCount()))
                        .ToListAsync(ct)
                    : await query
                        .GroupBy(e => new { e.CreatedAt.Year, e.CreatedAt.Month, e.CreatedAt.Day, e.EventName, Key = e.ReleaseSha })
                        .Select(g => new TrendRow(
                            g.Key.Year, g.Key.Month, g.Key.Day, 0, g.Key.EventName, g.Key.Key,
                            g.Select(x => x.DistinctId).Distinct().LongCount()))
                        .ToListAsync(ct),
                _ => hourKeyed
                    ? await query
                        .GroupBy(e => new { e.CreatedAt.Year, e.CreatedAt.Month, e.CreatedAt.Day, e.CreatedAt.Hour, e.EventName, Key = e.EndpointGroup })
                        .Select(g => new TrendRow(
                            g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, g.Key.EventName, g.Key.Key,
                            uniqueUsers ? g.Select(x => x.DistinctId).Distinct().LongCount() : g.LongCount()))
                        .ToListAsync(ct)
                    : await query
                        .GroupBy(e => new { e.CreatedAt.Year, e.CreatedAt.Month, e.CreatedAt.Day, e.EventName, Key = e.EndpointGroup })
                        .Select(g => new TrendRow(
                            g.Key.Year, g.Key.Month, g.Key.Day, 0, g.Key.EventName, g.Key.Key,
                            g.Select(x => x.DistinctId).Distinct().LongCount()))
                        .ToListAsync(ct),
            };
        }

        // Roll raw rows up to the requested interval bucket.
        DateTime BucketOf(TrendRow r)
        {
            var t = new DateTime(r.Year, r.Month, r.Day, r.Hour, 0, 0, DateTimeKind.Utc);
            return resolvedInterval switch
            {
                "hour" => t,
                "day" => t.Date,
                _ => range.From.Date.AddDays(Math.Floor((t.Date - range.From.Date).TotalDays / 7) * 7),
            };
        }

        var grouped = raw
            .GroupBy(r => new { Bucket = BucketOf(r), r.EventName, r.Key })
            .Select(g => new { g.Key.Bucket, g.Key.EventName, g.Key.Key, C = g.Sum(x => x.C) })
            .ToList();

        // Series totals. For counts, buckets sum cleanly; for unique_users they do NOT (a user
        // active in N buckets would count N times), so totals come from a separate range-wide
        // COUNT(DISTINCT) query keyed the same way as the series.
        Dictionary<(string EventName, string? Key), long>? distinctTotals = null;
        if (uniqueUsers)
        {
            var totalRows = breakdown switch
            {
                null => await query
                    .GroupBy(e => new { e.EventName, Key = (string?)null })
                    .Select(g => new { g.Key.EventName, g.Key.Key, C = g.Select(x => x.DistinctId).Distinct().LongCount() })
                    .ToListAsync(ct),
                "feature_area" => await query
                    .GroupBy(e => new { e.EventName, Key = e.FeatureArea })
                    .Select(g => new { g.Key.EventName, g.Key.Key, C = g.Select(x => x.DistinctId).Distinct().LongCount() })
                    .ToListAsync(ct),
                "release_sha" => await query
                    .GroupBy(e => new { e.EventName, Key = e.ReleaseSha })
                    .Select(g => new { g.Key.EventName, g.Key.Key, C = g.Select(x => x.DistinctId).Distinct().LongCount() })
                    .ToListAsync(ct),
                _ => await query
                    .GroupBy(e => new { e.EventName, Key = e.EndpointGroup })
                    .Select(g => new { g.Key.EventName, g.Key.Key, C = g.Select(x => x.DistinctId).Distinct().LongCount() })
                    .ToListAsync(ct),
            };
            distinctTotals = totalRows.ToDictionary(x => (x.EventName, x.Key), x => x.C);
        }

        long SeriesTotal(string eventName, string? key, long bucketSum) =>
            distinctTotals is null
                ? bucketSum
                : distinctTotals.GetValueOrDefault((eventName, key == "(none)" ? null : key), 0L);

        // With a breakdown, keep the top-N values by total and roll the tail into "other"
        // (dropped instead for unique_users, where distinct counts cannot be summed).
        string? RollKey(string? key, HashSet<string> top) =>
            key is not null && top.Contains(key) ? key : (key is null ? "(none)" : "other");

        List<TrendSeries> series;
        if (breakdown is null)
        {
            series = grouped
                .GroupBy(x => x.EventName)
                .Select(g => new TrendSeries(
                    g.Key,
                    null,
                    SeriesTotal(g.Key, null, g.Sum(x => x.C)),
                    g.OrderBy(x => x.Bucket).Select(x => new TrendBucket(x.Bucket, x.C)).ToArray()))
                .OrderByDescending(s => s.total)
                .ToList();
        }
        else
        {
            var topValues = grouped
                .Where(x => x.Key is not null)
                .GroupBy(x => x.Key!)
                .OrderByDescending(g => g.Sum(x => x.C))
                .Take(TrendMaxBreakdownValues)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.Ordinal);

            series = grouped
                .Select(x => new { x.Bucket, x.EventName, Key = RollKey(x.Key, topValues), x.C })
                .Where(x => !uniqueUsers || x.Key != "other")
                .GroupBy(x => new { x.EventName, x.Key })
                .Select(g => new TrendSeries(
                    g.Key.EventName,
                    g.Key.Key,
                    SeriesTotal(g.Key.EventName, g.Key.Key, g.Sum(x => x.C)),
                    g.GroupBy(x => x.Bucket)
                        .OrderBy(b => b.Key)
                        .Select(b => new TrendBucket(b.Key, b.Sum(x => x.C)))
                        .ToArray()))
                .OrderByDescending(s => s.total)
                .ToList();
        }

        return Results.Ok(new
        {
            range = new { from = range.From, to = range.To, interval = resolvedInterval },
            agg = uniqueUsers ? "unique_users" : "count",
            series,
        });
    }

    private sealed record TrendRow(int Year, int Month, int Day, int Hour, string EventName, string? Key, long C);
    private sealed record TrendBucket(DateTime t, long c);
    private sealed record TrendSeries(string @event, string? breakdown, long total, TrendBucket[] buckets);

    /// <summary>Annotations (deploy markers) in range, for chart overlays. Read-only on the dashboard.</summary>
    private static async Task<IResult> GetAnnotations(
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
        var rows = await db.Annotations.AsNoTracking()
            .Where(a => a.ApplicationId == appId && a.EnvironmentId == envId
                        && a.At >= range.From && a.At < range.To)
            .OrderBy(a => a.At)
            .Select(a => new { id = a.Id, at = a.At, label = a.Label, release_sha = a.ReleaseSha })
            .ToListAsync(ct);

        return Results.Ok(new { rows });
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
