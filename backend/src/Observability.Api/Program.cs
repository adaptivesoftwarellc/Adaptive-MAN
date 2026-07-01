using System.Text.Json;
using System.Text.Json.Serialization;
using Adaptive.ObservabilityClient;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Observability.Api.Configuration;
using Observability.Api.Endpoints;
using Observability.Api.Middleware;
using Observability.Domain.Identity;
using Observability.Infrastructure;
using Observability.Infrastructure.Authentication;
using Observability.Infrastructure.Hosting;
using Observability.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddKeyVaultIfConfigured();

builder.Services.AddObservabilityInfrastructure(builder.Configuration);

// Phase 8 background hosts — nightly retention sweep (8.5) and alert evaluator (8.3) now run in the
// API process. The dedicated Worker is not deployed by CI; folding them in ensures they actually run.
// Each self-gates on its Enabled option: retention defaults on (cheap, compliance), alerting defaults
// off (it polls the DB and would defeat serverless auto-pause until a tenant is live).
builder.Services.AddObservabilityBackgroundServices();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddProblemDetails();
builder.Services.AddObservabilityRateLimiting(builder.Configuration);

// Issue 10.8 — dogfood: register the SDK pointed at this same platform so its own unhandled
// server errors are reported as telemetry under the `adaptive-observability-meta` app. Bound to the
// `AdaptiveObservability` config section; `Enabled` defaults false (see appsettings) until the
// meta-app + a server key are provisioned via the 8.9 admin endpoint and the key is wired in.
// Disabled or unconfigured, every Capture/CaptureError is a no-op, so this is safe to always register.
builder.Services.AddAdaptiveObservability(builder.Configuration, "AdaptiveObservability");

var maxIngestBodyBytes = builder.Configuration.GetValue<long?>("Observability:Ingest:MaxBodyBytes")
    ?? IngestPayloadLimitMiddleware.DefaultMaxBodyBytes;

// Issue 10.4 — optional floor for the SDK version header. Unset by default: log-only, no rejection.
var minSdkVersion = builder.Configuration.GetValue<string?>("Observability:Sdk:MinVersion");

builder.Services.Configure<JsonOptions>(opts =>
{
    opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    opts.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    opts.SerializerOptions.PropertyNameCaseInsensitive = true;
    opts.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.ValidateRequiredSecrets();

{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
    // InMemory provider used by integration tests is non-relational; Migrate would throw.
    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
    }

    // Issue 8.6 — bootstrap the first Admin user from config when the Users table is empty, so a fresh
    // deployment has a way in without hand-seeded SQL. No-op unless Bootstrap:AdminEmail is set.
    await BootstrapAdminUser.SeedIfConfiguredAsync(
        db, scope.ServiceProvider.GetRequiredService<IPasswordHasher>(), app.Configuration, app.Logger);
}

app.UseMiddleware<CorrelationIdMiddleware>();

// Issue 10.8 — dogfood. Wraps the rest of the pipeline so unhandled exceptions are reported as
// `server_error_occurred` via the self-pointed SDK. Sits after CorrelationIdMiddleware so the
// emitted error carries the request's correlation id; excludes the ingest surface (loop guard).
app.UseMiddleware<ServerErrorTelemetryMiddleware>();

// UseRouting must run before UseRateLimiter so the limiter can read the endpoint's
// RequireRateLimiting metadata; otherwise the policy is silently never applied.
app.UseRouting();
app.UseRateLimiter();

// Ingest surface covers both the unprefixed paths and the /api/v1 mirror (Issue 10.4).
Func<HttpContext, bool> isIngestPath = ctx =>
    ctx.Request.Path.StartsWithSegments("/api/ingest") ||
    ctx.Request.Path.StartsWithSegments("/api/v1/ingest");

// 64 KB ingest payload cap (Issue 8.8), scoped to the ingest surface only.
app.UseWhen(isIngestPath, branch => branch.UseMiddleware<IngestPayloadLimitMiddleware>(maxIngestBodyBytes));

// SDK version header negotiation (Issue 10.4) — log-only; never rejects.
// Empty string (not null) as the floor arg: a null explicit arg can't be bound by the middleware
// activator, and SdkVersionMiddleware treats empty/unparseable as "no floor".
app.UseWhen(isIngestPath, branch => branch.UseMiddleware<SdkVersionMiddleware>(minSdkVersion ?? string.Empty));

// CORS for the ingest surface — applies in ALL environments. Browser SDKs send from an app origin
// (e.g. https://ivr.strategicsolutionsco.com) using a PublicClient key, so the ingest endpoints must
// answer CORS preflight and reflect the caller's Origin (and allow the custom X-Observability-Key
// header). The API key is the security boundary here — not CORS; ingest is write-only and
// fire-and-forget. Preflight (OPTIONS) carries no API key, so the app can't be resolved at that point;
// reflecting Origin is the standard approach for key-authenticated public ingest. (AppEnvironment
// .AllowedOriginsJson can tighten this per-app later, enforced on the keyed POST rather than preflight.)
app.UseWhen(isIngestPath, branch => branch.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin))
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
        ctx.Response.Headers.Append("Vary", "Origin");
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Observability-Key, X-Correlation-Id, X-Observability-SDK-Version";
        ctx.Response.Headers["Access-Control-Max-Age"] = "600";
    }
    if (HttpMethods.IsOptions(ctx.Request.Method)) { ctx.Response.StatusCode = 204; return; }
    await next();
}));

// CORS for the dashboard during local dev. Phase 8 RBAC will gate dashboard endpoints; until then
// the dashboard is open within the trusted network. Scoped to non-ingest so it doesn't override the
// per-origin ingest policy above with a wildcard.
if (app.Environment.IsDevelopment())
{
    app.UseWhen(ctx => !isIngestPath(ctx), branch => branch.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Observability-Key, X-Observability-Admin-Key, X-Correlation-Id, X-Observability-SDK-Version";
        if (HttpMethods.IsOptions(ctx.Request.Method)) { ctx.Response.StatusCode = 204; return; }
        await next();
    }));
}

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapDashboardEndpoints();
app.MapAdminEndpoints();
app.MapExportEndpoints();

var ingest = app.MapGroup("/api/ingest").AddApiKeyAuth().RequireRateLimiting(RateLimitingExtensions.IngestPolicy);
ingest.MapIngestionEndpoints();
ingest.MapSessionIngestEndpoints();

// Issue 10.4 — /api/v1 mirror of the ingest surface. Same handlers, same auth + rate limit;
// existing unprefixed routes remain as backwards-compatible aliases of v1.
var ingestV1 = app.MapGroup("/api/v1/ingest").AddApiKeyAuth().RequireRateLimiting(RateLimitingExtensions.IngestPolicy);
ingestV1.MapIngestionEndpoints();
ingestV1.MapSessionIngestEndpoints();

app.MapSessionReadEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapDevEndpoints();
}

app.Run();

public partial class Program { }
