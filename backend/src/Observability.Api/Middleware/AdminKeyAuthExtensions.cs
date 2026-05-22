using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Observability.Api.Middleware;

public static class AdminKeyAuthExtensions
{
    public const string HeaderName = "X-Observability-Admin-Key";
    public const string ConfigKey = "Observability:AdminApiKey";

    public static RouteGroupBuilder AddAdminKeyAuth(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var configured = http.RequestServices.GetRequiredService<IConfiguration>()[ConfigKey];
            if (string.IsNullOrWhiteSpace(configured))
            {
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            }
            if (!http.Request.Headers.TryGetValue(HeaderName, out var header) || string.IsNullOrWhiteSpace(header))
            {
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            }
            if (!FixedTimeEquals(header.ToString(), configured))
            {
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            }

            return await next(ctx);
        });

        return group;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ab.Length != bb.Length) return false;
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
