using System.Text.Json;
using Observability.Api.Middleware;
using Observability.Domain.Audit;
using Observability.Infrastructure.Persistence;

namespace Observability.Api.Endpoints;

/// <summary>
/// Shared <see cref="AuditLog"/> writer. The actor (admin_key / admin_user / user) is taken from the
/// request, set by the auth filters in <see cref="UserAuthExtensions"/>. Adds the row to the context
/// only — the caller owns <c>SaveChangesAsync</c> so the audit row commits in the same transaction as
/// the action it records.
/// </summary>
internal static class AuditWriter
{
    public static void Add(
        ObservabilityDbContext db,
        HttpContext http,
        string action,
        Guid? appId,
        Guid? envId,
        object details)
    {
        var actor = http.GetAuditActor();
        db.AuditLogs.Add(new AuditLog
        {
            Action = action,
            ActorType = actor?.Type ?? "system",
            ApplicationId = appId,
            EnvironmentId = envId,
            CorrelationId = http.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString(),
            DetailsJson = JsonSerializer.Serialize(details),
        });
    }
}
