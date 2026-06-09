namespace Observability.Domain.Identity;

/// <summary>
/// Issue 8.6 — a local platform user. Identity source is local-users for now (decision recorded in
/// docs/architecture.md); the persistence and enforcement are identity-source agnostic, so an Entra
/// adapter can be added later without touching this model.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Login identifier, unique (case-insensitive, stored lowercased).</summary>
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Encoded PBKDF2 hash produced by the password hasher; never the plaintext.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public Role Role { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    /// <summary>Apps this user owns. Only meaningful for <see cref="Role.AppOwner"/>.</summary>
    public ICollection<UserApplicationAssignment> ApplicationAssignments { get; set; }
        = new List<UserApplicationAssignment>();
}
