using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Adaptive.ObservabilityClient.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Adaptive.ObservabilityClient;

/// <summary>
/// Implementation of <see cref="IAnalyticsService"/> that ships events to the Adaptive
/// Observability ingestion API. Async, non-blocking — Capture/CaptureError enqueue and
/// return immediately. A background reader drains the channel.
/// </summary>
public sealed class AdaptiveObservabilityService : IAnalyticsService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Issue 10.4 — sent on every ingest request for wire-protocol version negotiation. Platform-tagged
    // per docs/api-contract.md (e.g. "dotnet/0.2.0"). Read once from the assembly's informational version.
    internal const string SdkVersionHeaderName = "X-Observability-SDK-Version";
    private static readonly string SdkVersionHeaderValue =
        "dotnet/" + (typeof(AdaptiveObservabilityService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AdaptiveObservabilityService).Assembly.GetName().Version?.ToString()
            ?? "unknown");

    private readonly AdaptiveObservabilityOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<AdaptiveObservabilityService> _logger;
    private readonly Channel<QueuedEnvelope> _channel;
    private readonly BackgroundJobDeduper _dedup;
    private readonly Task _drainTask;
    private readonly CancellationTokenSource _cts = new();

    public AdaptiveObservabilityService(
        IOptions<AdaptiveObservabilityOptions> options,
        HttpClient http,
        ILogger<AdaptiveObservabilityService> logger)
    {
        _options = options.Value;
        _http = http;
        _logger = logger;
        _dedup = new BackgroundJobDeduper(_options.BackgroundJobDedupWindow);
        _channel = Channel.CreateBounded<QueuedEnvelope>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        if (!string.IsNullOrEmpty(_options.HostUrl))
        {
            _http.BaseAddress = new Uri(_options.HostUrl.EndsWith('/') ? _options.HostUrl : _options.HostUrl + "/");
        }
        if (!string.IsNullOrEmpty(_options.ApiKey) && !_http.DefaultRequestHeaders.Contains("X-Observability-Key"))
        {
            _http.DefaultRequestHeaders.Add("X-Observability-Key", _options.ApiKey);
        }
        if (!_http.DefaultRequestHeaders.Contains(SdkVersionHeaderName))
        {
            _http.DefaultRequestHeaders.Add(SdkVersionHeaderName, SdkVersionHeaderValue);
        }

        _drainTask = _options.Enabled ? Task.Run(() => DrainAsync(_cts.Token)) : Task.CompletedTask;
    }

    public void Capture(string eventName, string distinctId, IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (!_options.Enabled) return;
        try
        {
            var props = MergeReleaseSha(properties);
            var env = new EventEnvelope
            {
                Event = eventName,
                DistinctId = distinctId,
                OccurredAt = DateTime.UtcNow,
                Properties = props,
            };
            _channel.Writer.TryWrite(new QueuedEnvelope(EnvelopeKind.Event, env, 0));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AdaptiveObservability Capture swallowed");
        }
    }

    public void CaptureError(string errorType, string distinctId, string? exceptionType = null, IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (!_options.Enabled) return;
        try
        {
            // Dedup background_job_failed by (job_name, error_type) over the configured window.
            if (properties is not null && properties.TryGetValue("job_name", out var jobNameObj) && jobNameObj is string jobName)
            {
                if (!_dedup.ShouldEmit(jobName, errorType)) return;
            }

            var props = MergeReleaseSha(properties);
            var env = new ErrorEnvelope
            {
                ErrorType = errorType,
                ExceptionType = exceptionType,
                DistinctId = distinctId,
                OccurredAt = DateTime.UtcNow,
                Properties = props,
            };
            _channel.Writer.TryWrite(new QueuedEnvelope(EnvelopeKind.Error, env, 0));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AdaptiveObservability CaptureError swallowed");
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _channel.Writer.TryComplete();
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        try
        {
            await _drainTask.WaitAsync(_options.ShutdownTimeout, combined.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug("AdaptiveObservability shutdown drain timeout/cancel");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await ShutdownAsync().ConfigureAwait(false); }
        catch { /* never throw from Dispose */ }
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        var buffer = new List<QueuedEnvelope>(_options.BatchSize);
        var flushTimer = new PeriodicTimer(_options.FlushInterval);

        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    buffer.Add(item);
                    if (buffer.Count >= _options.BatchSize) break;
                }
                if (buffer.Count > 0)
                {
                    await SendBatchAsync(buffer, ct).ConfigureAwait(false);
                    buffer.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* shutdown */
        }
        finally
        {
            // Drain any remaining items on shutdown.
            while (_channel.Reader.TryRead(out var item)) buffer.Add(item);
            if (buffer.Count > 0)
            {
                try { await SendBatchAsync(buffer, CancellationToken.None).ConfigureAwait(false); }
                catch { /* never throw */ }
            }
            flushTimer.Dispose();
        }
    }

    private async Task SendBatchAsync(IReadOnlyList<QueuedEnvelope> batch, CancellationToken ct)
    {
        foreach (var item in batch)
        {
            var path = item.Kind == EnvelopeKind.Event ? "api/ingest/events" : "api/ingest/errors";
            try
            {
                using var res = await _http.PostAsJsonAsync(path, item.Payload, Json, ct).ConfigureAwait(false);
                if ((int)res.StatusCode is >= 500 and < 600)
                {
                    if (item.Attempts + 1 <= _options.MaxRetries)
                    {
                        await Task.Delay(BackoffDelay(item.Attempts + 1), ct).ConfigureAwait(false);
                        _channel.Writer.TryWrite(item with { Attempts = item.Attempts + 1 });
                    }
                }
                // 4xx: terminal — server rejected payload. Server already wrote a SafetyViolation if applicable.
            }
            catch (HttpRequestException) when (item.Attempts + 1 <= _options.MaxRetries)
            {
                await Task.Delay(BackoffDelay(item.Attempts + 1), ct).ConfigureAwait(false);
                _channel.Writer.TryWrite(item with { Attempts = item.Attempts + 1 });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AdaptiveObservability send swallowed");
            }
        }
    }

    private static TimeSpan BackoffDelay(int attempt)
    {
        var baseMs = Math.Min(30_000, 250 * (int)Math.Pow(2, attempt - 1));
        var jitter = Random.Shared.Next(0, baseMs / 3);
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }

    private IReadOnlyDictionary<string, object?> MergeReleaseSha(IReadOnlyDictionary<string, object?>? incoming)
    {
        if (string.IsNullOrEmpty(_options.ReleaseSha))
        {
            return incoming ?? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>();
        }
        var dict = incoming is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(incoming);
        dict.TryAdd("release_sha", _options.ReleaseSha);
        return dict;
    }
}
