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

// CORS for the dashboard during local dev. Phase 8 RBAC will gate dashboard endpoints; until then
// the dashboard is open within the trusted network.
if (app.Environment.IsDevelopment())
{
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Observability-Key, X-Correlation-Id";
        if (HttpMethods.IsOptions(ctx.Request.Method)) { ctx.Response.StatusCode = 204; return; }
        await next();
    });
}

app.MapHealthEndpoints();
app.MapDashboardEndpoints();
app.MapAdminEndpoints();

var ingest = app.MapGroup("/api/ingest").AddApiKeyAuth();
ingest.MapIngestionEndpoints();
ingest.MapSessionIngestEndpoints();
app.MapSessionReadEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapDevEndpoints();
}

app.Run();

public partial class Program { }
