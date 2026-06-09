using System.Text.Json;
using Microsoft.Extensions.Logging;
using Observability.Domain.Telemetry;

namespace Observability.Application.Ingestion;

public interface IIngestionService
{
    Task<IngestionResult> IngestEventAsync(EventIngestionRequest request, IngestionContext context, CancellationToken ct);
    Task<IngestionResult> IngestErrorAsync(ErrorIngestionRequest request, IngestionContext context, CancellationToken ct);
}

public sealed class IngestionService : IIngestionService
{
    private readonly IPropertyAllowlistValidator _validator;
    private readonly IIngestionStore _store;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        IPropertyAllowlistValidator validator,
        IIngestionStore store,
        ILogger<IngestionService> logger)
    {
        _validator = validator;
        _store = store;
        _logger = logger;
    }

    public async Task<IngestionResult> IngestEventAsync(EventIngestionRequest request, IngestionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Event) || string.IsNullOrWhiteSpace(request.DistinctId))
        {
            return new IngestionResult(IngestionOutcome.SchemaError, null, "missing_event_or_distinct_id");
        }

        var validation = _validator.Validate(request.Event, request.Properties);
        if (!validation.IsValid)
        {
            if (validation.Reason == "forbidden_field")
            {
                await _store.AddSafetyViolationAsync(new SafetyViolation
                {
                    ApplicationId = context.ApplicationId,
                    EnvironmentId = context.EnvironmentId,
                    EventName = request.Event,
                    RejectedField = validation.RejectedField ?? "(unknown)",
                    Reason = validation.Reason,
                }, ct);

                _logger.LogWarning(
                    "SafetyViolation app={App} env={Env} event={Event} field={Field}",
                    context.ApplicationId, context.EnvironmentId, request.Event, validation.RejectedField);
            }

            return new IngestionResult(IngestionOutcome.AllowlistViolation, validation.RejectedField, validation.Reason);
        }

        var props = validation.SafeProperties;
        var record = new EventRecord
        {
            ApplicationId = context.ApplicationId,
            EnvironmentId = context.EnvironmentId,
            EventName = request.Event,
            DistinctId = request.DistinctId,
            SessionId = request.SessionId,
            CorrelationId = context.CorrelationId,
            NormalizedRoute = TryGetString(props, "normalized_route"),
            EndpointGroup = TryGetString(props, "endpoint_group"),
            FeatureArea = TryGetString(props, "feature_area"),
            ReleaseSha = TryGetString(props, "release_sha"),
            PropertiesJson = JsonSerializer.Serialize(props),
            OccurredAt = request.OccurredAt ?? DateTime.UtcNow,
        };

        await _store.AddEventAsync(record, ct);
        if (!string.IsNullOrEmpty(request.SessionId))
        {
            await _store.BumpSessionAsync(context.ApplicationId, context.EnvironmentId, request.SessionId, record.OccurredAt, isError: false, ct);
        }
        return new IngestionResult(IngestionOutcome.Accepted);
    }

    public async Task<IngestionResult> IngestErrorAsync(ErrorIngestionRequest request, IngestionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ErrorType) || string.IsNullOrWhiteSpace(request.DistinctId))
        {
            return new IngestionResult(IngestionOutcome.SchemaError, null, "missing_error_type_or_distinct_id");
        }

        // Errors share the same allowlist; pick the catalog event that best matches the error origin.
        // Default to server_error_occurred for validation purposes when caller doesn't specify.
        var classifyingEvent = !string.IsNullOrEmpty(request.ExceptionType) ? "server_error_occurred"
            : (request.Properties?.ContainsKey("job_name") == true ? "background_job_failed" : "frontend_exception");

        // Top-level DTO fields (exception_type, error_type) feed the same catalog allowlist as
        // properties — merge them in so validation sees a single picture.
        var mergedProps = request.Properties is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(request.Properties);
        if (!string.IsNullOrEmpty(request.ExceptionType))
        {
            mergedProps["exception_type"] = JsonDocument.Parse($"\"{request.ExceptionType}\"").RootElement;
        }
        if (!string.IsNullOrEmpty(request.ErrorType))
        {
            mergedProps["error_type"] = JsonDocument.Parse($"\"{request.ErrorType}\"").RootElement;
        }

        var validation = _validator.Validate(classifyingEvent, mergedProps);
        if (!validation.IsValid)
        {
            if (validation.Reason == "forbidden_field")
            {
                await _store.AddSafetyViolationAsync(new SafetyViolation
                {
                    ApplicationId = context.ApplicationId,
                    EnvironmentId = context.EnvironmentId,
                    EventName = classifyingEvent,
                    RejectedField = validation.RejectedField ?? "(unknown)",
                    Reason = validation.Reason,
                }, ct);
            }

            return new IngestionResult(IngestionOutcome.AllowlistViolation, validation.RejectedField, validation.Reason);
        }

        var props = validation.SafeProperties;
        var endpointGroup = TryGetString(props, "endpoint_group");
        var jobName = TryGetString(props, "job_name");
        var releaseSha = TryGetString(props, "release_sha");
        var fingerprint = ErrorFingerprint.Compute(request.ErrorType, request.ExceptionType, endpointGroup, jobName);

        var record = new ErrorRecord
        {
            ApplicationId = context.ApplicationId,
            EnvironmentId = context.EnvironmentId,
            Fingerprint = fingerprint,
            FingerprintVersion = ErrorFingerprint.CurrentVersion,
            ErrorType = request.ErrorType,
            ExceptionType = request.ExceptionType,
            EndpointGroup = endpointGroup,
            JobName = jobName,
            NormalizedRoute = TryGetString(props, "normalized_route"),
            HttpStatusCode = TryGetInt(props, "http_status_code"),
            ReleaseSha = releaseSha,
            PropertiesJson = JsonSerializer.Serialize(props),
            FirstSeenAt = request.OccurredAt ?? DateTime.UtcNow,
            LastSeenAt = request.OccurredAt ?? DateTime.UtcNow,
            LastCorrelationId = context.CorrelationId,
        };

        await _store.UpsertErrorAsync(record, ct);

        // Sidecar incident row for background_job_failed so the alert engine + dashboard can
        // rate-limit and group on (JobName, ErrorType) without scanning the global Errors table.
        // Disjoint from the session bump below: BG-job failures use system:* distinct ids and
        // never carry a session_id, while FE/server errors with session_id never have a job_name.
        if (!string.IsNullOrEmpty(jobName))
        {
            var bgFailure = new BackgroundJobFailure
            {
                ApplicationId = context.ApplicationId,
                EnvironmentId = context.EnvironmentId,
                JobName = jobName,
                ErrorType = request.ErrorType,
                Fingerprint = fingerprint,
                ReleaseSha = releaseSha,
                FirstSeenAt = record.FirstSeenAt,
                LastSeenAt = record.LastSeenAt,
            };
            await _store.UpsertBackgroundJobFailureAsync(bgFailure, BackgroundJobDedupWindow, ct);
        }

        if (!string.IsNullOrEmpty(request.SessionId))
        {
            await _store.BumpSessionAsync(context.ApplicationId, context.EnvironmentId, request.SessionId, record.LastSeenAt, isError: true, ct);
        }

        return new IngestionResult(IngestionOutcome.Accepted);
    }

    /// <summary>
    /// Default dedup window for background_job_failed alerting suppression.
    /// Mirrors the SDK-side default; per-app override is Phase 8.2.
    /// </summary>
    public static readonly TimeSpan BackgroundJobDedupWindow = TimeSpan.FromMinutes(15);

    private static string? TryGetString(IReadOnlyDictionary<string, JsonElement> props, string key) =>
        props.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? TryGetInt(IReadOnlyDictionary<string, JsonElement> props, string key) =>
        props.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
