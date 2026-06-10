namespace Observability.Domain.Alerting;

/// <summary>
/// Supported alert rule kinds (Issue 8.3). Each value selects a distinct evaluation strategy in the
/// Worker's alert evaluator. Stored as <c>int</c> so the wire/DB representation is stable.
/// </summary>
public enum AlertRuleType
{
    /// <summary>Fire when the count of matching events in the window meets or exceeds the threshold.</summary>
    CountOverWindow = 1,

    /// <summary>Fire when a new error fingerprint first appears within the window carrying a release SHA.</summary>
    NewErrorAfterRelease = 2,

    /// <summary>Fire when active-error volume as a percentage of event volume in the window meets the threshold.</summary>
    ErrorRateAboveThreshold = 3,

    /// <summary>Fire on any background-job failure seen within the window in a Production environment.</summary>
    AnyProdJobFailure = 4,
}
