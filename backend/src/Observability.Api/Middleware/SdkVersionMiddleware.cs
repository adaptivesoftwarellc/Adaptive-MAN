namespace Observability.Api.Middleware;

/// <summary>
/// Issue 10.4 — reads the <c>X-Observability-SDK-Version</c> header on ingest requests for
/// wire-protocol version negotiation. Logs at <c>Information</c> when the header is missing (the SDK
/// version is treated as "unknown") and at <c>Warning</c> when it parses below a configured floor.
/// It never rejects: floor enforcement only becomes meaningful with a v2 wire protocol, which is too
/// early to enforce.
/// Scoped to the ingest surface (both unprefixed and <c>/api/v1</c>) via <c>UseWhen</c> in Program.cs.
/// </summary>
public sealed class SdkVersionMiddleware
{
    public const string HeaderName = "X-Observability-SDK-Version";

    private readonly RequestDelegate _next;
    private readonly ILogger<SdkVersionMiddleware> _logger;
    private readonly Version? _floor;

    public SdkVersionMiddleware(RequestDelegate next, ILogger<SdkVersionMiddleware> logger, string? minVersion)
    {
        _next = next;
        _logger = logger;
        _floor = TryParseVersion(minVersion);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // CORS preflight never carries custom headers — evaluating it would log a false "missing"
        // entry for every browser preflight. Skip it.
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var raw = context.Request.Headers.TryGetValue(HeaderName, out var header) ? header.ToString() : null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Information, not Warning: until the deployed fleet upgrades, 100% of ingest traffic is
            // header-less, so a Warning here would flood logs. Warning is reserved for below-floor.
            _logger.LogInformation(
                "Ingest request missing {Header}; treating SDK version as unknown. Path={Path}",
                HeaderName, context.Request.Path);
        }
        else if (_floor is not null && TryParseVersion(raw) is { } parsed && parsed < _floor)
        {
            _logger.LogWarning(
                "Ingest request from SDK version {SdkVersion} is below the configured floor {Floor}. Path={Path}",
                raw, _floor, context.Request.Path);
        }

        await _next(context);
    }

    /// <summary>
    /// Accepts a bare semver ("0.2.0") or a platform-tagged value ("js/0.2.0", "dotnet/0.2.0") —
    /// the segment after the last '/' is parsed. Pre-release suffixes that don't parse return null
    /// (no floor comparison is attempted, log-only behavior is unaffected).
    /// </summary>
    private static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var slash = value.LastIndexOf('/');
        var versionPart = slash >= 0 ? value[(slash + 1)..] : value;
        return Version.TryParse(versionPart, out var v) ? v : null;
    }
}
