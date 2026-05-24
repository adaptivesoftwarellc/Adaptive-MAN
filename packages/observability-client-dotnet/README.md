# AdaptiveSoftwareLLC.ObservabilityClient (.NET)

> NuGet package id is `AdaptiveSoftwareLLC.ObservabilityClient`; the .NET namespace remains `Adaptive.ObservabilityClient`. The `Adaptive.*` prefix on nuget.org is reserved by another account.


ASP.NET Core SDK for the Adaptive Observability platform. Ships an `IAnalyticsService` implementation whose contract is identical to SCH's existing `SCH.Core.Interfaces.IAnalyticsService`, so cutover is a DI registration swap — no call sites change.

## Install

```bash
dotnet add package AdaptiveSoftwareLLC.ObservabilityClient
```

## Quickstart (under 50 LOC)

```csharp
// Program.cs
using Adaptive.ObservabilityClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAdaptiveObservability(builder.Configuration);
// or:
builder.Services.AddAdaptiveObservability(opts =>
{
    opts.HostUrl = "https://observability.example.com";
    opts.ApiKey = builder.Configuration["AdaptiveObservability:ApiKey"]!;
    opts.Environment = builder.Environment.EnvironmentName;
    opts.ReleaseSha = Environment.GetEnvironmentVariable("RELEASE_SHA");
});
```

```jsonc
// appsettings.json
{
  "AdaptiveObservability": {
    "Enabled": true,
    "HostUrl": "https://observability.example.com",
    "ApiKey": "<from-key-vault>",
    "Environment": "Production",
    "BackgroundJobDedupWindow": "00:15:00"
  }
}
```

```csharp
// any service / controller / background worker
public sealed class NightlyImport(IAnalyticsService analytics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { /* ... */ }
        catch (Exception ex)
        {
            analytics.CaptureError(
                errorType: ex.GetType().Name,
                distinctId: "system:background-service",
                exceptionType: ex.GetType().FullName,
                properties: new Dictionary<string, object?>
                {
                    ["job_name"] = "nightly-import",
                    ["error_type"] = ex.GetType().Name,
                });
        }
    }
}
```

## API surface

| Member | Notes |
|---|---|
| `IAnalyticsService.Capture(eventName, distinctId, properties)` | Async, non-blocking. Background channel drains. |
| `IAnalyticsService.CaptureError(errorType, distinctId, exceptionType, properties)` | Never throws into the host app. BG-job failures dedup client-side. |
| `IAnalyticsService.ShutdownAsync(ct)` | Wired to `IHostedService.StopAsync` automatically. |
| `RouteNormalizer.Normalize(path)` | Strips IDs/UUIDs/ULIDs/hex tokens. Preserves `posthog-500-test`. |
| `RouteNormalizer.NormalizeFromContext(httpContext)` | Path-based fallback. |
| `RouteNormalizer.EndpointGroup(normalizedRoute)` | Maps to `auth`, `users`, `orders`, etc. |

## PostHog migration cheatsheet (SCH-side)

```diff
  // Program.cs
- builder.Services.Configure<AnalyticsOptions>(builder.Configuration.GetSection("PostHog"));
- builder.Services.AddSingleton<IAnalyticsService, PostHogService>();
+ builder.Services.AddAdaptiveObservability(builder.Configuration.GetSection("AdaptiveObservability"));
```

No call site in `GlobalExceptionMiddleware.cs` or any background service changes — all consumers use `IAnalyticsService` and don't see the implementation.

For the dual-write composite window (Phase 6.6), register both:

```csharp
builder.Services.AddSingleton<PostHogService>();
builder.Services.AddSingleton<AdaptiveObservabilityService>();
builder.Services.AddSingleton<IAnalyticsService>(sp => new CompositeAnalyticsService(
    sp.GetRequiredService<PostHogService>(),
    sp.GetRequiredService<AdaptiveObservabilityService>()));
```

(`CompositeAnalyticsService` lives in SCH for the cutover window — it's not part of this SDK.)

## Privacy

The SDK does not sanitize. The server's allowlist validator (`PropertyAllowlistValidator`) is the canonical filter — forbidden fields produce a `SafetyViolation` row server-side. See [`docs/privacy-rules.md`](../../docs/privacy-rules.md).
