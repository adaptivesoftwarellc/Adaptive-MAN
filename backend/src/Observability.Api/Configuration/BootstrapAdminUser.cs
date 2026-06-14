using Microsoft.EntityFrameworkCore;
using Observability.Domain.Identity;
using Observability.Infrastructure.Authentication;
using Observability.Infrastructure.Persistence;

namespace Observability.Api.Configuration;

/// <summary>
/// Issue 8.6 — seeds the first Admin user from configuration on startup when no users exist yet, so a
/// fresh deployment can be administered without hand-written SQL. Idempotent: skips once any user
/// exists, and is a no-op unless <c>Observability:Bootstrap:AdminEmail</c> is set. Production should
/// supply the password via Key Vault (<c>Observability--Bootstrap--AdminPassword</c>) and rotate it
/// after first login.
/// </summary>
public static class BootstrapAdminUser
{
    public static async Task SeedIfConfiguredAsync(
        ObservabilityDbContext db, IPasswordHasher hasher, IConfiguration config, ILogger logger)
    {
        var email = config["Observability:Bootstrap:AdminEmail"];
        var password = config["Observability:Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        if (await db.Users.AnyAsync())
            return;

        var normalized = email.Trim().ToLowerInvariant();
        var entry = db.Users.Add(new User
        {
            Email = normalized,
            DisplayName = config["Observability:Bootstrap:AdminDisplayName"]?.Trim() ?? "Administrator",
            PasswordHash = hasher.Hash(password),
            Role = Role.Admin,
        });

        try
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Bootstrapped initial Admin user {Email} (Users table was empty).", normalized);
        }
        catch (DbUpdateException)
        {
            // Concurrent startup of another instance won the race and seeded first; the unique index on
            // Email rejects this insert. That's the intended outcome — treat "already seeded" as success
            // rather than crashing this instance's startup.
            entry.State = EntityState.Detached;
            logger.LogInformation("Bootstrap admin already seeded by another instance; skipping.");
        }
    }
}
