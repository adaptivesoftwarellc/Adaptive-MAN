using Adaptive.ObservabilityClient;

namespace Observability.Api.Middleware;

/// <summary>
/// Issue 10.8 dogfood — the platform's own <c>GlobalExceptionMiddleware</c>. Catches unhandled
/// exceptions, emits a <c>server_error_occurred</c> error through the registered
/// <see cref="IAnalyticsService"/> (the dogfood SDK, pointed at this same API), then re-throws so the
/// normal 500 response path is unchanged. Ported in shape from SCH_API's middleware.
///
/// PII safety: only the catalog-allowed fields leave the process — <c>exception_type</c>,
/// <c>endpoint_group</c>, <c>http_status_code</c>, <c>correlation_id</c>. Never the exception
/// message, stack trace, or any unnormalized route segment (see docs/event-catalog.md).
///
/// Loop guard: the ingest surface is excluded. If an exception bubbles out of <c>/api/ingest</c> we
/// still return 500, but we do NOT emit — otherwise a broken ingest path would have the SDK POST a new
/// error back to that same path, which 500s again, and so on. The SDK swallowing transport failures is
/// the backstop (it never CaptureErrors its own send failures), but skipping the surface entirely is
/// the primary guard. Documented in docs/architecture.md.
/// </summary>
public sealed class ServerErrorTelemetryMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAnalyticsService _analytics;

    public ServerErrorTelemetryMiddleware(RequestDelegate next, IAnalyticsService analytics)
    {
        _next = next;
        _analytics = analytics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // Only true unhandled exceptions reach here (5xx). 4xx and expected business results never
            // throw, so they are never reported — matching the catalog's "True 500 ... Not 4xx".
            if (EmitsForPath(context.Request.Path))
            {
                Emit(context, ex);
            }
            throw;
        }
    }

    /// <summary>
    /// Loop guard: errors on the ingest surface are never reported. The SDK delivers a
    /// <c>server_error_occurred</c> by POSTing it to ingest, so emitting on an ingest failure would
    /// feed a failing path back into itself. Public for direct testing of the guard rule.
    /// </summary>
    public static bool EmitsForPath(PathString path) =>
        !path.StartsWithSegments("/api/ingest")
        && !path.StartsWithSegments("/api/v1/ingest");

    private void Emit(HttpContext context, Exception ex)
    {
        var normalized = RouteNormalizer.NormalizeFromContext(context);
        var endpointGroup = RouteNormalizer.EndpointGroup(normalized);
        var correlationId = context.Items[CorrelationIdMiddleware.HttpItemKey] as string;

        _analytics.CaptureError(
            errorType: ex.GetType().Name,
            distinctId: DistinctId(context),
            exceptionType: ex.GetType().FullName,
            properties: new Dictionary<string, object?>
            {
                ["endpoint_group"] = endpointGroup,
                ["http_status_code"] = 500,
                ["correlation_id"] = correlationId,
            });
    }

    // Identity rules: a resolved ingest client is api_client_{appId}; everything else (dashboard,
    // anonymous, pre-auth) is anon. The platform never attaches a human distinct id to its own faults.
    private static string DistinctId(HttpContext context) =>
        context.Items.TryGetValue(ApiKeyAuthExtensions.HttpItemKey, out var raw)
        && raw is Observability.Infrastructure.Authentication.ResolvedApiKey key
            ? $"api_client_{key.ApplicationId}"
            : "anon";
}
