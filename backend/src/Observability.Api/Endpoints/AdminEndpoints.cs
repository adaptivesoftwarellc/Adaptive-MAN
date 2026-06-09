using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Observability.Api.Middleware;
using Observability.Application.Ingestion;
using Observability.Domain.Applications;
using Observability.Domain.Identity;
using Observability.Infrastructure.Authentication;
using Observability.Infrastructure.Persistence;
using DomainApplication = Observability.Domain.Applications.Application;

namespace Observability.Api.Endpoints;

/// <summary>
/// Phase 8.9 admin provisioning. Removes the hand-seeded SQL dependency for onboarding new apps and
/// minting API keys. As of Issue 8.6 RBAC the surface is gated by <c>AddAdminAuth</c> — an Admin-role
/// bearer token, or the static admin key (KV secret <c>ObservabilityAdminKey</c>) as a
/// break-glass/bootstrap path for provisioning the first admin user.
/// </summary>
public static class AdminEndpoints
{
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").AddAdminAuth();
        admin.MapGet("/apps", ListApps);
        admin.MapPost("/apps", CreateApp);
        admin.MapGet("/apps/{slug}/environments/{env}/keys", ListKeys);
        admin.MapPost("/apps/{slug}/environments/{env}/keys", MintKey);
        admin.MapPost("/apps/{slug}/environments/{env}/keys/{id:guid}/revoke", RevokeKey);
        admin.MapGet("/audit", GetAudit);
        admin.MapPost("/fingerprints/backfill", BackfillFingerprints);

        // Issue 8.6 — user administration. Provisioning the first admin uses the break-glass admin key.
        admin.MapGet("/users", ListUsers);
        admin.MapPost("/users", CreateUser);
        admin.MapPost("/users/{id:guid}/applications", AssignApplication);
    }

    /// <summary>
    /// Issue 10.6 admin inventory. Lists every app with its environments and per-environment key counts
    /// (active vs total) so the admin Apps page can show onboarding state at a glance. Unlike the
    /// reader-scoped <c>/api/apps</c>, this is the full Admin view — no canary hiding, no app-scope filter.
    /// </summary>
    private static async Task<IResult> ListApps(ObservabilityDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var apps = await db.Applications.AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new
            {
                id = a.Id,
                slug = a.Slug,
                name = a.Name,
                description = a.Description,
                is_active = a.IsActive,
                environments = a.Environments
                    .OrderBy(e => e.EnvironmentName)
                    .Select(e => new
                    {
                        id = e.Id,
                        name = e.EnvironmentName,
                        is_active = e.IsActive,
                        total_key_count = db.ApiKeys.Count(k => k.EnvironmentId == e.Id),
                        active_key_count = db.ApiKeys.Count(k =>
                            k.EnvironmentId == e.Id
                            && k.RevokedAt == null
                            && (k.ExpiresAt == null || k.ExpiresAt > now)),
                    })
                    .ToList(),
            })
            .ToListAsync(ct);

        return Results.Ok(new { apps });
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
            CreatedByUserId = http.GetAuditActor()?.Email ?? "admin_key",
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

    /// <summary>
    /// Issue 10.6 — keys for one app+environment. Read-only: the plaintext is gone after minting, so this
    /// returns the row id (used for revoke), type, created / last-used / revoked timestamps and an
    /// is_active flag. The UI masks the id for display.
    /// </summary>
    private static async Task<IResult> ListKeys(
        string slug,
        string env,
        ObservabilityDbContext db,
        CancellationToken ct)
    {
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var environment = await db.AppEnvironments.AsNoTracking()
            .Where(e => e.Application!.Slug == normalizedSlug && e.EnvironmentName == env)
            .Select(e => new { e.Id })
            .FirstOrDefaultAsync(ct);
        if (environment is null)
            return Results.NotFound(new { error = "not_found", reason = "app or environment not found." });

        var now = DateTime.UtcNow;
        var keys = await db.ApiKeys.AsNoTracking()
            .Where(k => k.EnvironmentId == environment.Id)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new
            {
                id = k.Id,
                key_type = k.KeyType == ApiKeyType.PublicClient ? "PublicClient" : "ServerApi",
                created_at = k.CreatedAt,
                last_used_at = k.LastUsedAt,
                expires_at = k.ExpiresAt,
                revoked_at = k.RevokedAt,
                is_active = k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now),
            })
            .ToListAsync(ct);

        return Results.Ok(new { keys });
    }

    /// <summary>
    /// Issue 10.6 — revoke a key. Idempotent: revoking an already-revoked key is a no-op that returns the
    /// existing revoked time. Writes an <c>admin.key.revoked</c> audit row (closes the 8.7 note). After
    /// this, <see cref="Observability.Infrastructure.Authentication.ApiKeyResolver"/> rejects the key.
    /// </summary>
    private static async Task<IResult> RevokeKey(
        string slug,
        string env,
        Guid id,
        ObservabilityDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var environment = await db.AppEnvironments.AsNoTracking()
            .Where(e => e.Application!.Slug == normalizedSlug && e.EnvironmentName == env)
            .Select(e => new { e.Id })
            .FirstOrDefaultAsync(ct);
        if (environment is null)
            return Results.NotFound(new { error = "not_found", reason = "app or environment not found." });

        var key = await db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.EnvironmentId == environment.Id, ct);
        if (key is null)
            return Results.NotFound(new { error = "not_found", reason = "key not found for that app and environment." });

        var alreadyRevoked = key.RevokedAt is not null;
        if (!alreadyRevoked)
        {
            key.RevokedAt = DateTime.UtcNow;
            await WriteAuditAsync(db, "admin.key.revoked", key.ApplicationId, key.EnvironmentId, http, new
            {
                key_id = key.Id,
                key_type = key.KeyType.ToString(),
            }, ct);
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new { id = key.Id, revoked_at = key.RevokedAt, already_revoked = alreadyRevoked });
    }

    public sealed record BackfillFingerprintsRequest(int? BatchSize);

    /// <summary>
    /// Issue 8.1 fingerprint backfill. Re-stamps <c>Errors</c> rows left on an older fingerprint
    /// algorithm version up to the current one, merging any that collide onto a shared fingerprint.
    /// Idempotent — a no-op once every row is on the current version. Writes an
    /// <c>admin.fingerprint.backfilled</c> audit row with the result counts.
    /// </summary>
    private static async Task<IResult> BackfillFingerprints(
        [FromBody] BackfillFingerprintsRequest? req,
        IErrorFingerprintBackfiller backfiller,
        ObservabilityDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await backfiller.BackfillAsync(req?.BatchSize ?? 500, ct);

        await WriteAuditAsync(db, "admin.fingerprint.backfilled", null, null, http, new
        {
            scanned = result.Scanned,
            updated = result.Updated,
            merged = result.Merged,
            target_version = result.TargetVersion,
        }, ct);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            scanned = result.Scanned,
            updated = result.Updated,
            merged = result.Merged,
            target_version = result.TargetVersion,
        });
    }

    /// <summary>
    /// Issue 8.7 read-only audit view. Paginated, filterable list of <c>AuditLogs</c> rows for the
    /// Phase 10.6 admin UI. Same admin-key gate as the write endpoints; pagination/range semantics
    /// mirror <see cref="DashboardEndpoints"/> and the response envelope matches
    /// <c>/api/dashboard/events</c>. Pure read — no rows written.
    /// </summary>
    private static async Task<IResult> GetAudit(
        [FromQuery] string? action,
        [FromQuery] string? app,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? page,
        [FromQuery(Name = "page_size")] int? pageSize,
        ObservabilityDbContext db,
        CancellationToken ct)
    {
        var range = ResolveRange(from, to);
        var (skip, take) = ResolvePaging(page, pageSize);

        var q = db.AuditLogs.AsNoTracking()
            .Where(a => a.OccurredAt >= range.From && a.OccurredAt < range.To);

        if (!string.IsNullOrWhiteSpace(action))
            q = q.Where(a => a.Action == action);

        if (!string.IsNullOrWhiteSpace(app))
        {
            Guid? appId;
            if (Guid.TryParse(app, out var parsedId))
            {
                appId = parsedId;
            }
            else
            {
                var slug = app.Trim().ToLowerInvariant();
                appId = await db.Applications.AsNoTracking()
                    .Where(a => a.Slug == slug)
                    .Select(a => (Guid?)a.Id)
                    .FirstOrDefaultAsync(ct);

                // Unknown slug is a filter that matches nothing, not a 404.
                if (appId is null)
                    return Results.Ok(new { total = 0L, page = skip / take, page_size = take, rows = Array.Empty<object>() });
            }
            q = q.Where(a => a.ApplicationId == appId);
        }

        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(a => a.OccurredAt)
            .Skip(skip).Take(take)
            .Select(a => new
            {
                id = a.Id,
                occurred_at = a.OccurredAt,
                action = a.Action,
                actor_type = a.ActorType,
                application_id = a.ApplicationId,
                environment_id = a.EnvironmentId,
                correlation_id = a.CorrelationId,
                details_json = a.DetailsJson,
            })
            .ToListAsync(ct);

        return Results.Ok(new { total, page = skip / take, page_size = take, rows });
    }

    private static async Task<IResult> ListUsers(ObservabilityDbContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.Email)
            .Select(u => new
            {
                id = u.Id,
                email = u.Email,
                display_name = u.DisplayName,
                role = u.Role.ToString(),
                is_active = u.IsActive,
                created_at = u.CreatedAt,
                last_login_at = u.LastLoginAt,
                owned_application_ids = u.ApplicationAssignments.Select(a => a.ApplicationId).ToArray(),
            })
            .ToListAsync(ct);

        return Results.Ok(new { users });
    }

    public sealed record CreateUserRequest(string? Email, string? DisplayName, string? Password, string? Role);

    private static async Task<IResult> CreateUser(
        [FromBody] CreateUserRequest? req,
        ObservabilityDbContext db,
        IPasswordHasher hasher,
        HttpContext http,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Results.BadRequest(new { error = "invalid_request", reason = "email and password are required." });

        if (!TryParseRole(req.Role, out var role))
            return Results.BadRequest(new { error = "invalid_request", reason = "role must be one of: viewer, developer, app_owner, admin." });

        var email = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Results.Conflict(new { error = "conflict", reason = "a user with that email already exists." });

        var user = new User
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? email : req.DisplayName.Trim(),
            PasswordHash = hasher.Hash(req.Password),
            Role = role,
        };
        db.Users.Add(user);

        await WriteAuditAsync(db, "admin.user.created", null, null, http, new
        {
            user_id = user.Id,
            email = user.Email,
            role = role.ToString(),
        }, ct);
        await db.SaveChangesAsync(ct);

        return Results.Json(new { id = user.Id, email = user.Email, role = role.ToString() }, statusCode: 201);
    }

    public sealed record AssignApplicationRequest(Guid? ApplicationId);

    /// <summary>Grants an AppOwner read access to one application (Issue 8.6 scoping).</summary>
    private static async Task<IResult> AssignApplication(
        Guid id,
        [FromBody] AssignApplicationRequest? req,
        ObservabilityDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        if (req?.ApplicationId is not { } appId)
            return Results.BadRequest(new { error = "invalid_request", reason = "application_id is required." });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return Results.NotFound(new { error = "not_found", reason = "user not found." });

        if (!await db.Applications.AnyAsync(a => a.Id == appId, ct))
            return Results.NotFound(new { error = "not_found", reason = "application not found." });

        var exists = await db.UserApplicationAssignments
            .AnyAsync(a => a.UserId == id && a.ApplicationId == appId, ct);
        if (!exists)
        {
            db.UserApplicationAssignments.Add(new UserApplicationAssignment { UserId = id, ApplicationId = appId });
            await WriteAuditAsync(db, "admin.user.assignment.added", appId, null, http, new
            {
                user_id = id,
                application_id = appId,
            }, ct);
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new { user_id = id, application_id = appId, created = !exists });
    }

    private static bool TryParseRole(string? value, out Role role)
    {
        role = default;
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "viewer": role = Role.Viewer; return true;
            case "developer": role = Role.Developer; return true;
            case "app_owner" or "appowner": role = Role.AppOwner; return true;
            case "admin": role = Role.Admin; return true;
            default: return false;
        }
    }

    private static (DateTime From, DateTime To) ResolveRange(DateTime? from, DateTime? to)
    {
        var resolvedTo = to ?? DateTime.UtcNow;
        var resolvedFrom = from ?? resolvedTo.AddHours(-24);
        if (resolvedFrom >= resolvedTo) resolvedFrom = resolvedTo.AddHours(-24);
        return (DateTime.SpecifyKind(resolvedFrom, DateTimeKind.Utc), DateTime.SpecifyKind(resolvedTo, DateTimeKind.Utc));
    }

    private static (int Skip, int Take) ResolvePaging(int? page, int? pageSize)
    {
        var take = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var p = Math.Max(page ?? 0, 0);
        return (p * take, take);
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
        AuditWriter.Add(db, http, action, appId, envId, details);
        return Task.CompletedTask;
    }
}
