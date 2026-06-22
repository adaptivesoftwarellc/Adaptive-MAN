namespace Adaptive.ObservabilityClient;

/// <summary>
/// Follows the PostHog Phase 1 <c>AnalyticsOptions</c> shape so adopting from PostHog is a config-section rename.
/// </summary>
public sealed class AdaptiveObservabilityOptions
{
    /// <summary>
    /// Off by default so a host that binds a config section omitting <c>Enabled</c> fails safe (emits
    /// nothing) rather than silently self-reporting. Hosts opt in explicitly via config or code.
    /// </summary>
    public bool Enabled { get; set; } = false;
    public string HostUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Environment { get; set; } = "Development";
    public string? ReleaseSha { get; set; }
    public int BatchSize { get; set; } = 50;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxRetries { get; set; } = 3;

    /// <summary>Background-job dedup window; suppresses identical (job_name, error_type) failures.</summary>
    public TimeSpan BackgroundJobDedupWindow { get; set; } = TimeSpan.FromMinutes(15);
}
