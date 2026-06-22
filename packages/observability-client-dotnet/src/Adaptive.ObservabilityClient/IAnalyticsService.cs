namespace Adaptive.ObservabilityClient;

/// <summary>
/// Adoption seam for the Adaptive Observability platform.
/// Follows the PostHog Phase 1 analytics contract, so a caller already on PostHog can swap
/// implementations via DI without changing call sites, and a greenfield tenant has a small
/// stable interface to depend on.
/// </summary>
public interface IAnalyticsService
{
    /// <summary>Emits an analytics event. Never throws into the host app.</summary>
    void Capture(string eventName, string distinctId, IReadOnlyDictionary<string, object?>? properties = null);

    /// <summary>Emits an error event. Never throws into the host app.</summary>
    void CaptureError(string errorType, string distinctId, string? exceptionType = null, IReadOnlyDictionary<string, object?>? properties = null);

    /// <summary>Awaits in-flight sends and stops the background channel.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
