# AdaptiveSoftwareLLC.ObservabilityClient (.NET)

> NuGet package id is `AdaptiveSoftwareLLC.ObservabilityClient`; the .NET namespace remains `Adaptive.ObservabilityClient`. The `Adaptive.*` prefix on nuget.org is reserved by another account.


ASP.NET Core SDK for the Adaptive Observability platform. Ships an `IAnalyticsService` implementation whose contract follows the PostHog Phase 1 catalog, so an app migrating off PostHog swaps one DI registration (no call sites change) and a greenfield tenant (e.g. WMSAPI) instruments against a small, stable interface.

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

## Failure modes

`Capture` / `CaptureError` write to a bounded in-memory channel and return immediately; a single background reader drains it. The SDK **never throws into the host app** — every enqueue and send path catches and `LogDebug`s. All behavior below is in [`AdaptiveObservabilityService.cs`](src/Adaptive.ObservabilityClient/AdaptiveObservabilityService.cs).

- **Bounded queue, oldest dropped.** Envelopes go into a `Channel<T>` bounded at **10,000 items** with `FullMode = DropOldest`. When the queue is full the **oldest queued envelope is silently dropped** to admit the new one. There is **no disk-backed persistence** — a process restart loses anything still queued.
- **5xx / network → retry.** A `5xx` response or an `HttpRequestException` re-enqueues the item with an incremented `Attempts` after a backoff, until `MaxRetries` (**default 3**) is exhausted, after which it is dropped. Backoff is `min(30_000, 250 * 2^(attempt-1))` ms plus jitter of `[0, baseMs/3)` — the same curve as the JS SDK.
- **4xx is terminal.** A `4xx` is treated as a permanent rejection (bad payload / auth / allowlist `SafetyViolation`) and the item is discarded without retry. The server records the `SafetyViolation` if applicable.
- **Batching.** The reader fills a buffer up to `BatchSize` (**default 50**) and flushes on a `FlushInterval` (**default 5s**) `PeriodicTimer`. Items are POSTed one-by-one within a batch.
- **Background-job dedup.** `CaptureError` calls carrying a `job_name` property are deduped client-side by `(job_name, error_type)` over `BackgroundJobDedupWindow` (**default 15 min**) — duplicates inside the window are dropped before they ever enter the channel. (Server-side dedup hardening is tracked separately in Phase 8.2.)
- **Shutdown.** `ShutdownAsync` (wired to `IHostedService.StopAsync`) completes the channel and waits up to `ShutdownTimeout` (**default 10s**) for the reader to drain remaining items; on timeout the drain is abandoned. `DisposeAsync` calls it and never throws.
- **Disabled.** With `Enabled = false` the drain task never starts and `Capture`/`CaptureError` short-circuit to no-ops.

> *Future enhancement:* there is no status/drop callback today. If added (e.g. an `Action<TransportStatus>` option) it would be additive and non-breaking.

## Troubleshooting: events don't appear

1. **Is it enabled and configured?** Confirm `AdaptiveObservability:Enabled` is `true` and `HostUrl` / `ApiKey` are set (non-Dev: resolved from Key Vault). With `Enabled = false` nothing is sent by design.
2. **Raise the log level to `Debug`.** All swallowed failures (`send swallowed`, `Capture swallowed`, shutdown drain timeout) log at `Debug` under the `Adaptive.ObservabilityClient` category — the only window into silent loss.
3. **4xx from the API?** `401` = bad/revoked key; `400`/`422` = a forbidden property rejected by the server allowlist — check [`docs/privacy-rules.md`](../../docs/privacy-rules.md) and look for a `SafetyViolation` row. 4xx items are dropped, not retried.
4. **Low-volume process?** A batch flushes at 50 items or every 5s. Short-lived jobs may exit before a flush — rely on `ShutdownAsync` (auto-wired) to drain on host stop, and give it up to `ShutdownTimeout`.
5. **High-volume burst with the API down?** The channel caps at 10,000 and drops oldest-first, so a sustained outage loses the earliest events. Confirmed by `send swallowed` debug logs.
6. **Missing background-job errors?** Identical `(job_name, error_type)` failures are deduped for 15 min by default. Widen or narrow `BackgroundJobDedupWindow` if expected failures are being suppressed.

## PostHog migration cheatsheet

For an app already on PostHog, cutover is one DI registration:

```diff
  // Program.cs
- builder.Services.Configure<AnalyticsOptions>(builder.Configuration.GetSection("PostHog"));
- builder.Services.AddSingleton<IAnalyticsService, PostHogService>();
+ builder.Services.AddAdaptiveObservability(builder.Configuration.GetSection("AdaptiveObservability"));
```

No call site in your exception middleware or background services changes — all consumers use `IAnalyticsService` and don't see the implementation. Greenfield tenants (e.g. WMSAPI) skip this entirely and just add the `AddAdaptiveObservability(...)` line.

## Privacy

The SDK does not sanitize. The server's allowlist validator (`PropertyAllowlistValidator`) is the canonical filter — forbidden fields produce a `SafetyViolation` row server-side. See [`docs/privacy-rules.md`](../../docs/privacy-rules.md).
