using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Observability.Api.Middleware;
using Observability.Domain.Identity;
using Observability.Infrastructure.Authentication;
using Observability.Infrastructure.Persistence;

namespace Observability.Api.Endpoints;

/// <summary>
/// Issue 8.6 — local-user authentication. <c>POST /api/auth/login</c> exchanges credentials for a
/// bearer token; <c>GET /api/auth/me</c> returns the current principal for the dashboard to gate UI on.
/// This is the local-users implementation of the identity seam (decision in docs/architecture.md).
/// </summary>
public static class AuthEndpoints
{
    public sealed record LoginRequest(string? Email, string? Password);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", Login);

        var me = app.MapGroup("/api/auth").AddRequireUser();
        me.MapGet("/me", Me);
    }

    private static async Task<IResult> Login(
        [FromBody] LoginRequest? req,
        ObservabilityDbContext db,
        IPasswordHasher hasher,
        IAccessTokenService tokens,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Results.BadRequest(new { error = "invalid_request", reason = "email and password are required." });

        var email = req.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // Always run a verify (against a real or dummy hash) so a missing user and a wrong password
        // take the same time — no account-enumeration oracle.
        var hashToCheck = user?.PasswordHash ?? DummyHash;
        var ok = hasher.Verify(req.Password, hashToCheck);

        if (user is null || !user.IsActive || !ok)
            return Results.Json(new { error = "invalid_credentials" }, statusCode: 401);

        var (token, expiresAt) = tokens.Issue(user);
        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            token,
            expires_at = expiresAt,
            user = new
            {
                email = user.Email,
                display_name = user.DisplayName,
                role = user.Role.ToString(),
            },
        });
    }

    private static IResult Me(HttpContext http)
    {
        var user = http.GetUser();
        return Results.Ok(new
        {
            email = user.Email,
            role = user.Role.ToString(),
            can_access_admin = user.CanAccessAdmin,
            owned_application_ids = user.OwnedApplicationIds,
        });
    }

    // A precomputed PBKDF2 hash of a random string; never matches a real password. Keeps the
    // no-such-user branch on the same code path as a real verification.
    private const string DummyHash =
        "pbkdf2$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
}
