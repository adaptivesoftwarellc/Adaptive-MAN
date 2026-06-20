using Observability.Application.Ingestion;

namespace Observability.Api.Endpoints;

/// <summary>
/// Development-only smoke test endpoint. Mounted only when env=Development in Program.cs.
/// Exercises the ingest path end-to-end without a real tenant.
/// </summary>
public static class DevEndpoints
{
    public static void MapDevEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        app.MapPost("/api/dev/smoke-test", async (
            HttpContext http,
            IIngestionService service,
            CancellationToken ct) =>
        {
            // Self-emit using the dev_smoke_test event. Caller still needs a valid Dev API key on the header
            // because this routes through the same auth filter as /api/ingest/* in real usage; here it's
            // exempt so a fresh dev environment can confirm connectivity before keys exist.
            var ctx = new IngestionContext(
                Guid.Empty, Guid.Empty,
                (string?)http.Items["CorrelationId"] ?? Guid.NewGuid().ToString("N"));

            var result = await service.IngestEventAsync(
                new EventIngestionRequest(
                    Event: "dev_smoke_test",
                    DistinctId: "test:dev",
                    SessionId: null,
                    OccurredAt: DateTime.UtcNow,
                    Properties: null),
                ctx, ct);

            return Results.Ok(new { outcome = result.Outcome.ToString() });
        });

        // Issue 10.8 — dogfood verify hook. Forces an unhandled exception so the
        // ServerErrorTelemetryMiddleware emits a `server_error_occurred` through the self-pointed SDK,
        // landing a server-category error row under the `adaptive-observability-meta` app. Dev-only.
        app.MapGet("/api/dev/throw", void (string? kind) =>
        {
            throw kind switch
            {
                "timeout" => new TimeoutException("dev-forced timeout"),
                "invalid" => new InvalidOperationException("dev-forced invalid operation"),
                _ => new Exception("dev-forced unhandled exception"),
            };
        });
    }
}
