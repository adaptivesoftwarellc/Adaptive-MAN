using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Observability.Api.Middleware;
using Observability.Domain.Applications;
using Observability.Domain.Audit;
using Observability.Infrastructure.Authentication;
using Observability.Infrastructure.Persistence;
using DomainApplication = Observability.Domain.Applications.Application;

namespace Observability.Api.Endpoints;

/// <summary>
/// Phase 8.9 admin provisioning. Removes the hand-seeded SQL dependency for onboarding new apps and
/// minting API keys. Gated by a static admin key (KV secret <c>ObservabilityAdminKey</c>) until
/// Phase 8.6 RBAC lands; the endpoint shape stays stable across that swap.
/// </summary>
public static class AdminEndpoints
{
    private const string ActorType = "admin_key";

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").AddAdminKeyAuth();
        admin.MapPost("/apps", CreateApp);
        admin.MapPost("/apps/{slug}/environments/{env}/keys", MintKey);
    }

    public sealed record CreateAppRequest(string Name, string Slug, string? Description, string[]? Environments);

    private static async Task<IResult> CreateApp(
        [FromBody] CreateAppRequest? req,
        ObservabilityDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Slug) || string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.BadRequest(new { error = "invalid_request", reason = "name and slug are required." });
        }

        var slug = req.Slug.Trim().ToLowerInvariant();
        var envs = (req.Environments ?? new[] { "Development", "UAT", "Production" })
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var existing = await db.Applications
            .Include(a => a.Environments)
            .FirstOrDefaultAsync(a => a.Slug == slug, ct);

        bool created;
        DomainApplication app;
        if (existing is null)
        {
            app = new DomainApplication
            {
                Name = req.Name.Trim(),
                Slug = slug,
                Description = req.Description?.Trim(),
            };
            foreach (var envName in envs)
            {
                app.Environments.Add(new AppEnvironment
                {
                    ApplicationId = app.Id,
                    EnvironmentName = envName,
                });
            }
            db.Applications.Add(app);
            created = true;
        }
        else
        {
            app = existing;
            created = false;
        }

        await WriteAuditAsync(db, "admin.app.created", app.Id, null, http, new
        {
            slug = app.Slug,
            created,
            environments = app.Environments.Select(e => e.EnvironmentName).ToArray(),
        }, ct);

        await db.SaveChangesAsync(ct);

        return Results.Json(new
        {
            id = app.Id,
            slug = app.Slug,
            name = app.Name,
            created,
            environments = app.Environments.Select(e => new { id = e.Id, name = e.EnvironmentName }).ToArray(),
        }, statusCode: created ? 201 : 200);
    }

    public sealed record MintKeyRequest(string KeyType);

    private static async Task<IResult> MintKey(
        string slug,
        string env,
        [FromBody] MintKeyRequest? req,
        ObservabilityDbContext db,
        IApiKeyGenerator generator,
        IApiKeyHasher hasher,
        HttpContext http,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.KeyType))
        {
            return Results.BadRequest(new { error = "invalid_request", reason = "key_type is required." });
        }

        var keyType = req.KeyType.Trim().ToLowerInvariant() switch
        {
            "public_client" or "publicclient" => ApiKeyType.PublicClient,
            "server_api" or "serverapi" => ApiKeyType.ServerApi,
            _ => (ApiKeyType?)null,
        };
        if (keyType is null)
        {
            return Results.BadRequest(new { error = "invalid_request", reason = "key_type must be 'public_client' or 'server_api'." });
        }

        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var environment = await db.AppEnvironments
            .Include(e => e.Application)
            .FirstOrDefaultAsync(
                e => e.Application!.Slug == normalizedSlug && e.EnvironmentName == env,
                ct);
        if (environment is null)
        {
            return Results.NotFound(new { error = "not_found", reason = "app or environment not found." });
        }

        var plaintext = generator.Generate(keyType.Value);
        var key = new ApiKey
        {
            ApplicationId = environment.ApplicationId,
            EnvironmentId = environment.Id,
            KeyHash = hasher.Hash(plaintext),
            KeyType = keyType.Value,
            CreatedByUserId = ActorType,
        };
        db.ApiKeys.Add(key);

        await WriteAuditAsync(db, "admin.key.minted", environment.ApplicationId, environment.Id, http, new
        {
            key_id = key.Id,
            key_type = keyType.Value.ToString(),
        }, ct);

        await db.SaveChangesAsync(ct);

        return Results.Json(new
        {
            id = key.Id,
            key_type = keyType.Value.ToString(),
            plaintext_key = plaintext,
            note = "Store this immediately. The plaintext value is not retrievable after this response.",
        }, statusCode: 201);
    }

    private static Task WriteAuditAsync(
        ObservabilityDbContext db,
        string action,
        Guid? appId,
        Guid? envId,
        HttpContext http,
        object details,
        CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Action = action,
            ActorType = ActorType,
            ApplicationId = appId,
            EnvironmentId = envId,
            CorrelationId = http.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString(),
            DetailsJson = JsonSerializer.Serialize(details),
        });
        return Task.CompletedTask;
    }
}
