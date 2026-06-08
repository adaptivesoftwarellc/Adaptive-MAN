using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Adaptive.ObservabilityClient.Tests;

/// <summary>
/// Issue 10.4 — the SDK sets X-Observability-SDK-Version as a default request header so every
/// ingest request carries it.
/// </summary>
public class SdkVersionHeaderTests
{
    [Fact]
    public async Task Sets_platform_tagged_version_header_on_the_http_client()
    {
        using var http = new HttpClient();
        var options = Options.Create(new AdaptiveObservabilityOptions
        {
            Enabled = false, // no background drain — we only inspect header setup in the ctor
            HostUrl = "http://localhost",
            ApiKey = "aoserv_test",
        });

        await using var service = new AdaptiveObservabilityService(options, http, NullLogger<AdaptiveObservabilityService>.Instance);

        Assert.True(http.DefaultRequestHeaders.TryGetValues(AdaptiveObservabilityService.SdkVersionHeaderName, out var values));
        var value = Assert.Single(values!);
        Assert.StartsWith("dotnet/", value);
    }
}
