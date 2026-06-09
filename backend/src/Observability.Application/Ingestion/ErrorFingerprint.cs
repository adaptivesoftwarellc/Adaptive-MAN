using System.Security.Cryptography;
using System.Text;

namespace Observability.Application.Ingestion;

/// <summary>
/// Server-side error fingerprinting (Issue 8.1). Centralizes the grouping-key algorithm and its
/// version so the two are never out of step: every row written by <see cref="Compute"/> is stamped
/// with <see cref="CurrentVersion"/>, and the backfiller recomputes any row left on an older version.
///
/// The fingerprint groups errors by the stable shape of the failure — <c>error_type</c>,
/// <c>exception_type</c>, <c>endpoint_group</c>, <c>job_name</c> — deliberately excluding volatile
/// fields (correlation id, release sha, timestamps) so the same fault collapses onto one row whose
/// <c>OccurrenceCount</c> is bumped on repeat. Inputs are pipe-joined with nulls normalized to empty
/// strings so the delimited shape is fixed; the SHA-256 digest is truncated to 32 hex chars (128
/// bits) to fit the 64-char <c>Fingerprint</c> column with collision headroom.
///
/// Bumping the algorithm: change the body of <see cref="Compute"/> and increment
/// <see cref="CurrentVersion"/> in the same change. The backfiller (`IErrorFingerprintBackfiller`)
/// then re-stamps historical rows, merging any that collide onto a now-shared fingerprint.
/// </summary>
public static class ErrorFingerprint
{
    /// <summary>
    /// Version of the algorithm implemented by <see cref="Compute"/>. Stored on every
    /// <c>ErrorRecord.FingerprintVersion</c>. Increment in lockstep with any change to the inputs
    /// or hashing below.
    /// </summary>
    public const int CurrentVersion = 1;

    public static string Compute(string errorType, string? exceptionType, string? endpointGroup, string? jobName)
    {
        var raw = string.Join('|', errorType, exceptionType ?? "", endpointGroup ?? "", jobName ?? "");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }
}
