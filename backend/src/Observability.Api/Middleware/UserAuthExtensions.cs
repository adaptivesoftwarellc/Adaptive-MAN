using System.Security.Cryptography;
using System.Text;
using Observability.Infrastructure.Authentication;

namespace Observability.Api.Middleware;

/// <summary>Who performed an audited action, captured by the auth filters for <see cref="Endpoints.AuditWriter"/>.</summary>
public sealed record AuditActor(string Type, string? Email);

/// <summary>
/// Issue 8.6 — bearer-token authentication and authorization filters for the RBAC surface.
/// <see cref="AddRequireUser"/> gates a group on any authenticated user; <see cref="AddAdminAuth"/>
/// gates the admin surface on the Admin role, with the legacy static admin key kept as a
/// break-glass/bootstrap path so the first admin user can be provisioned.
/// </summary>
public static class UserAuthExtensions
{
    public const string UserItemKey = "ObservabilityUser";
    public const string ActorItemKey = "ObservabilityAuditActor";

    public static RouteGroupBuilder AddRequireUser(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var user = await AuthenticateBearerAsync(ctx.HttpContext);
            if (user is null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);

            ctx.HttpContext.Items[UserItemKey] = user;
            ctx.HttpContext.Items[ActorItemKey] = new AuditActor("user", user.Email);
            return await next(ctx);
        });
        return group;
    }

    public static RouteGroupBuilder AddAdminAuth(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;

            // 1. Preferred: an Admin-role bearer token.
            var user = await AuthenticateBearerAsync(http);
            if (user is not null)
            {
                if (!user.CanAccessAdmin)
                    return Results.Json(new { error = "forbidden" }, statusCode: 403);

                http.Items[UserItemKey] = user;
                http.Items[ActorItemKey] = new AuditActor("admin_user", user.Email);
                return await next(ctx);
            }

            // 2. Break-glass / bootstrap: the legacy static admin key (KV secret ObservabilityAdminKey).
            var configured = http.RequestServices.GetRequiredService<IConfiguration>()[AdminKeyAuthExtensions.ConfigKey];
            if (!string.IsNullOrWhiteSpace(configured)
                && http.Request.Headers.TryGetValue(AdminKeyAuthExtensions.HeaderName, out var hdr)
                && !string.IsNullOrWhiteSpace(hdr)
                && FixedTimeEquals(hdr.ToString(), configured))
            {
                http.Items[ActorItemKey] = new AuditActor("admin_key", null);
                return await next(ctx);
            }

            return Results.Json(new { error = "unauthorized" }, statusCode: 401);
        });
        return group;
    }

    private static async Task<AuthenticatedUser?> AuthenticateBearerAsync(HttpContext http)
    {
        var token = ExtractBearer(http);
        if (token is null) return null;

        var authenticator = http.RequestServices.GetRequiredService<IUserAuthenticator>();
        return await authenticator.AuthenticateAsync(token, http.RequestAborted);
    }

    private static string? ExtractBearer(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue("Authorization", out var header)) return null;
        var value = header.ToString();
        const string scheme = "Bearer ";
        if (!value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var token = value[scheme.Length..].Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>The authenticated user for a request gated by <see cref="AddRequireUser"/>.</summary>
    public static AuthenticatedUser GetUser(this HttpContext http) =>
        (AuthenticatedUser)http.Items[UserItemKey]!;

    public static AuthenticatedUser? GetUserOrNull(this HttpContext http) =>
        http.Items.TryGetValue(UserItemKey, out var u) ? u as AuthenticatedUser : null;

    public static AuditActor? GetAuditActor(this HttpContext http) =>
        http.Items.TryGetValue(ActorItemKey, out var a) ? a as AuditActor : null;

    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ab.Length != bb.Length) return false;
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
