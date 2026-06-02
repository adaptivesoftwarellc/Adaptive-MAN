using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Observability.Domain.Telemetry;
using Observability.Infrastructure.Persistence;
using Xunit;

namespace Observability.IntegrationTests;

/// <summary>
/// Pins the GET /api/dashboard/errors `category` filter. Category is not a stored column — the
/// endpoint derives it from which fields are populated (exception_type => server; else job_name =>
/// background_job; else frontend), which must stay in sync with the frontend's errorCategory().
/// </summary>
public class DashboardErrorsTests : IClassFixture<IngestionWebApplicationFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IngestionWebApplicationFactory _factory;

    public DashboardErrorsTests(IngestionWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Errors_endpoint_filters_by_derived_category()
    {
        await _factory.SeedAsync();

        var seenAt = DateTime.UtcNow.AddMinutes(-5); // safely inside the default 24h window
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ObservabilityDbContext>();
            db.Errors.AddRange(
                // server: has an exception_type
                NewError("fp-server", seenAt, errorType: "NullReferenceException",
                    exceptionType: "System.NullReferenceException", endpointGroup: "orders", httpStatusCode: 500),
                // frontend: no exception_type, no job_name
                NewError("fp-frontend", seenAt, errorType: "TypeError", normalizedRoute: "/orders/:id"),
                // background_job: no exception_type, has a job_name
                NewError("fp-bgjob", seenAt, errorType: "TimeoutException", jobName: "NightlySync"));
            await db.SaveChangesAsync();
        }

        var dashboard = _factory.CreateClient();
        var baseUrl = $"/api/dashboard/errors?app={_factory.SeededAppId}&env={_factory.SeededEnvId}";

        // No category -> all three rows.
        var all = await GetErrorsAsync(dashboard, baseUrl);
        Assert.Equal(3, all.Total);

        // server -> only the row with an exception_type.
        var server = await GetErrorsAsync(dashboard, $"{baseUrl}&category=server");
        Assert.Single(server.Rows);
        Assert.NotNull(server.Rows[0].ExceptionType);
        Assert.Null(server.Rows[0].JobName);

        // frontend -> only the row with neither exception_type nor job_name.
        var frontend = await GetErrorsAsync(dashboard, $"{baseUrl}&category=frontend");
        Assert.Single(frontend.Rows);
        Assert.Null(frontend.Rows[0].ExceptionType);
        Assert.Null(frontend.Rows[0].JobName);

        // background_job -> only the row with a job_name (and no exception_type).
        var bg = await GetErrorsAsync(dashboard, $"{baseUrl}&category=background_job");
        Assert.Single(bg.Rows);
        Assert.NotNull(bg.Rows[0].JobName);
        Assert.Null(bg.Rows[0].ExceptionType);
    }

    private ErrorRecord NewError(
        string fingerprint,
        DateTime seenAt,
        string errorType,
        string? exceptionType = null,
        string? endpointGroup = null,
        string? jobName = null,
        string? normalizedRoute = null,
        int? httpStatusCode = null) => new()
    {
        ApplicationId = _factory.SeededAppId,
        EnvironmentId = _factory.SeededEnvId,
        Fingerprint = fingerprint,
        ErrorType = errorType,
        ExceptionType = exceptionType,
        EndpointGroup = endpointGroup,
        JobName = jobName,
        NormalizedRoute = normalizedRoute,
        HttpStatusCode = httpStatusCode,
        FirstSeenAt = seenAt,
        LastSeenAt = seenAt,
    };

    private static async Task<ErrorsResponse> GetErrorsAsync(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ErrorsResponse>(body, Json)!;
    }

    private sealed record ErrorsResponse(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("rows")] List<ErrorRow> Rows);

    private sealed record ErrorRow(
        [property: JsonPropertyName("exception_type")] string? ExceptionType,
        [property: JsonPropertyName("job_name")] string? JobName);
}
