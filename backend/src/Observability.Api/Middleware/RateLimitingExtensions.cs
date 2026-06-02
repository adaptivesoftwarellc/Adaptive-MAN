using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Observability.Api.Middleware;

/// <summary>
/// Issue 8.8 — per-key fixed-window rate limiting for the ingest surface, using the framework's
/// built-in <c>Microsoft.AspNetCore.RateLimiting</c> (no third-party dependency). Rejections return
/// 429 with a <c>Retry-After</c> header.
/// </summary>
public sealed class RateLimitingOptions
{
    public int PermitLimit { get; set; } = 100;
    public int WindowSeconds { get; set; } = 10;
}

public static class RateLimitingExtensions
{
    public const string IngestPolicy = "ingest";
    private const string AnonymousPartition = "anonymous";

    public static IServiceCollection AddObservabilityRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        // Bind lazily so the values are read when options are first resolved (after all config
        // sources are merged), not eagerly at registration time. Defaults live on the options type.
        services.AddOptions<RateLimitingOptions>().Bind(config.GetSection("Observability:RateLimiting"));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                return ValueTask.CompletedTask;
            };

            options.AddPolicy(IngestPolicy, httpContext =>
            {
                var limits = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ResolvePartitionKey(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.PermitLimit,
                        Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    });
            });
        });

        return services;
    }

    /// <summary>
    /// The API key resolves in an endpoint filter that runs <em>after</em> this middleware, so the
    /// resolved <c>ApplicationId</c> isn't available yet. Partition on the raw key header instead
    /// (hashed so plaintext keys aren't held as in-memory partition keys). This is per-key limiting
    /// — the right DoS granularity — and avoids a second key resolution on the hot path. Keyless
    /// requests share one bucket so a keyless flood can't allocate unbounded partitions.
    /// </summary>
    private static string ResolvePartitionKey(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue(ApiKeyAuthExtensions.HeaderName, out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(header.ToString()));
            return Convert.ToHexString(bytes);
        }
        return AnonymousPartition;
    }
}
