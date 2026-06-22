using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Adaptive.ObservabilityClient;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AdaptiveObservabilityService"/> as the <see cref="IAnalyticsService"/>.
    /// Migrating from PostHog: replace the existing PostHog DI registration with this. Greenfield: just add it.
    /// </summary>
    public static IServiceCollection AddAdaptiveObservability(
        this IServiceCollection services,
        Action<AdaptiveObservabilityOptions> configure)
    {
        services.AddOptions<AdaptiveObservabilityOptions>().Configure(configure);
        return AddCore(services);
    }

    public static IServiceCollection AddAdaptiveObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "AdaptiveObservability")
    {
        services.AddOptions<AdaptiveObservabilityOptions>().Bind(configuration.GetSection(sectionName));
        return AddCore(services);
    }

    private static IServiceCollection AddCore(IServiceCollection services)
    {
        services.AddHttpClient<AdaptiveObservabilityService>();
        services.AddSingleton<IAnalyticsService>(sp => sp.GetRequiredService<AdaptiveObservabilityService>());
        services.AddHostedService<ShutdownHook>();
        return services;
    }

    private sealed class ShutdownHook : IHostedService
    {
        private readonly AdaptiveObservabilityService _svc;
        public ShutdownHook(AdaptiveObservabilityService svc) => _svc = svc;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => _svc.ShutdownAsync(ct);
    }
}
