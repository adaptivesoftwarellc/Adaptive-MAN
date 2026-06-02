using Microsoft.AspNetCore.Http.Features;

namespace Observability.Api.Middleware;

/// <summary>
/// Issue 8.8 — caps the ingest request body at a configured size. Runs before the endpoint reads
/// the body: a declared <c>Content-Length</c> over the cap is rejected immediately with 413; for
/// chunked requests we tighten Kestrel's per-request <see cref="IHttpMaxRequestBodySizeFeature"/>
/// so the read itself is bounded. Scoped to <c>/api/ingest</c> via <c>UseWhen</c> in Program.cs —
/// dashboard/admin payloads are unaffected.
/// </summary>
public sealed class IngestPayloadLimitMiddleware
{
    public const long DefaultMaxBodyBytes = 64 * 1024;

    private readonly RequestDelegate _next;
    private readonly long _maxBytes;

    public IngestPayloadLimitMiddleware(RequestDelegate next, long maxBytes)
    {
        _next = next;
        _maxBytes = maxBytes;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.ContentLength is { } length && length > _maxBytes)
        {
            await RejectAsync(context);
            return;
        }

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = _maxBytes;
        }

        await _next(context);
    }

    private static Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return context.Response.WriteAsJsonAsync(new { error = "payload_too_large" });
    }
}
