namespace Observability.Domain.Identity;

/// <summary>
/// Issue 8.6 — scopes an <see cref="Role.AppOwner"/> user to a single application. The presence of a
/// row grants that user read access to the app; absence means no access. Global-read roles
/// (Admin/Developer/Viewer) ignore this table.
/// </summary>
public class UserApplicationAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid ApplicationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
