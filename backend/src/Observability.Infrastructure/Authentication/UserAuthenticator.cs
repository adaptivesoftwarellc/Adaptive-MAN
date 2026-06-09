using Microsoft.EntityFrameworkCore;
using Observability.Domain.Identity;
using Observability.Infrastructure.Persistence;

namespace Observability.Infrastructure.Authentication;

/// <summary>
/// The authenticated principal for a request, with the authorization helpers the endpoints use.
/// Read policy (Issue 8.6): global-read roles see every app; AppOwner is limited to assigned apps.
/// </summary>
public sealed record AuthenticatedUser(
    Guid UserId,
    string Email,
    Role Role,
    IReadOnlyCollection<Guid> OwnedApplicationIds)
{
    public bool CanAccessAdmin => Role == Role.Admin;

    public bool IsGlobalReader => Role is Role.Admin or Role.Developer or Role.Viewer;

    /// <summary>Admin/Developer reads are audited (Issue 8.6 acceptance).</summary>
    public bool IsPrivileged => Role is Role.Admin or Role.Developer;

    public bool CanReadApplication(Guid applicationId) =>
        IsGlobalReader || OwnedApplicationIds.Contains(applicationId);
}

/// <summary>
/// Identity seam (Issue 8.6). Resolves a bearer token to an <see cref="AuthenticatedUser"/>. The local
/// implementation validates an <see cref="IAccessTokenService"/> token and loads the user + owned apps
/// from the DB; an Entra adapter would validate an AAD JWT and map group claims to roles here instead,
/// leaving every consumer unchanged.
/// </summary>
public interface IUserAuthenticator
{
    Task<AuthenticatedUser?> AuthenticateAsync(string? bearerToken, CancellationToken ct);
}

public sealed class LocalUserAuthenticator : IUserAuthenticator
{
    private readonly ObservabilityDbContext _db;
    private readonly IAccessTokenService _tokens;

    public LocalUserAuthenticator(ObservabilityDbContext db, IAccessTokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    public async Task<AuthenticatedUser?> AuthenticateAsync(string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return null;

        var claims = _tokens.Validate(bearerToken);
        if (claims is null) return null;

        // Re-check the user against the DB on every request so deactivation / role changes take effect
        // immediately rather than waiting for the token to expire.
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == claims.UserId && u.IsActive)
            .Select(u => new { u.Id, u.Email, u.Role })
            .FirstOrDefaultAsync(ct);
        if (user is null) return null;

        IReadOnlyCollection<Guid> owned = Array.Empty<Guid>();
        if (user.Role == Role.AppOwner)
        {
            owned = await _db.UserApplicationAssignments.AsNoTracking()
                .Where(a => a.UserId == user.Id)
                .Select(a => a.ApplicationId)
                .ToListAsync(ct);
        }

        return new AuthenticatedUser(user.Id, user.Email, user.Role, owned);
    }
}
