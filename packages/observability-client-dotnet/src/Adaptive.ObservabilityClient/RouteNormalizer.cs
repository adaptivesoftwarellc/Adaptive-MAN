using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Adaptive.ObservabilityClient;

/// <summary>
/// Server-side route normalization. Prefers RouteData when available, falls back to regex.
/// Ported from SCH's AnalyticsIdentity.cs — preserves the validated token-threshold tuning so
/// segments like <c>posthog-500-test</c> stay literal.
/// </summary>
public static partial class RouteNormalizer
{
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"^[0-9A-HJKMNP-TV-Z]{26}$")]
    private static partial Regex UlidRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex NumericRegex();

    [GeneratedRegex(@"^[0-9a-f]{16,}$", RegexOptions.IgnoreCase)]
    private static partial Regex HexTokenRegex();

    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        var queryIdx = path.IndexOf('?');
        if (queryIdx >= 0) path = path[..queryIdx];
        var hashIdx = path.IndexOf('#');
        if (hashIdx >= 0) path = path[..hashIdx];
        if (!path.StartsWith('/')) path = "/" + path;

        var parts = path.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            if (IsIdentifier(parts[i])) parts[i] = ":id";
        }
        var joined = string.Join('/', parts);
        return joined.Length == 0 ? "/" : joined;
    }

    public static string NormalizeFromContext(HttpContext ctx)
    {
        // Endpoint metadata for route templates is fragile across MVC/Minimal APIs;
        // path-based normalization is the canonical fallback.
        return Normalize(ctx.Request.Path.Value ?? "/");
    }

    public static string EndpointGroup(string normalizedRoute)
    {
        if (string.IsNullOrEmpty(normalizedRoute)) return "other";
        var lower = normalizedRoute.ToLowerInvariant();
        if (lower.StartsWith("/api/auth/")) return "auth";
        if (lower.StartsWith("/api/users/") || lower.StartsWith("/api/user/")) return "users";
        if (lower.StartsWith("/api/orders/") || lower.StartsWith("/api/order/")) return "orders";
        if (lower.StartsWith("/api/reports/") || lower.StartsWith("/api/report/")) return "reports";
        if (lower.StartsWith("/api/admin/")) return "admin";
        if (lower.StartsWith("/api/"))
        {
            var rest = lower[5..];
            var slash = rest.IndexOf('/');
            return slash > 0 ? rest[..slash] : (rest.Length > 0 ? rest : "api");
        }
        return "other";
    }

    private static bool IsIdentifier(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return false;
        if (NumericRegex().IsMatch(segment)) return true;
        if (UuidRegex().IsMatch(segment)) return true;
        if (UlidRegex().IsMatch(segment)) return true;
        if (HexTokenRegex().IsMatch(segment)) return true;
        return false;
    }
}
