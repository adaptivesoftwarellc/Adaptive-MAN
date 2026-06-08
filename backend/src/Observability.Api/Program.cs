using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Observability.Api.Configuration;
using Observability.Api.Endpoints;
using Observability.Api.Middleware;
using Observability.Infrastructure;
using Observability.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddKeyVaultIfConfigured();

builder.Services.AddObservabilityInfrastructure(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddProblemDetails();
builder.Services.AddObservabilityRateLimiting(builder.Configuration);

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
}

app.UseMiddleware<CorrelationIdMiddleware>();

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

// CORS for the dashboard during local dev. Phase 8 RBAC will gate dashboard endpoints; until then
// the dashboard is open within the trusted network.
if (app.Environment.IsDevelopment())
{
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Observability-Key, X-Correlation-Id, X-Observability-SDK-Version";
        if (HttpMethods.IsOptions(ctx.Request.Method)) { ctx.Response.StatusCode = 204; return; }
        await next();
    });
}

app.MapHealthEndpoints();
app.MapDashboardEndpoints();
app.MapAdminEndpoints();

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
