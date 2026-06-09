namespace Observability.Application.Ingestion;

/// <summary>
/// Recomputes fingerprints for <c>Errors</c> rows stamped with an algorithm version older than
/// <see cref="ErrorFingerprint.CurrentVersion"/> (Issue 8.1). Idempotent: a row already on the
/// current version is skipped, so re-running is a no-op once caught up.
///
/// When a recompute changes a row's fingerprint such that it now collides with another row in the
/// same tenant/env, the two are merged — occurrence counts are summed and the first/last-seen bounds
/// widened — so the unique <c>(ApplicationId, EnvironmentId, Fingerprint)</c> index is preserved and
/// no occurrence history is lost.
/// </summary>
public interface IErrorFingerprintBackfiller
{
    Task<FingerprintBackfillResult> BackfillAsync(int batchSize, CancellationToken ct);
}

/// <param name="Scanned">Rows found below the current fingerprint version.</param>
/// <param name="Updated">Rows re-stamped in place (fingerprint unchanged or no collision).</param>
/// <param name="Merged">Stale rows folded into an existing canonical row and deleted.</param>
/// <param name="TargetVersion">The version every scanned row was brought up to.</param>
public sealed record FingerprintBackfillResult(int Scanned, int Updated, int Merged, int TargetVersion);
