using Microsoft.Extensions.DependencyInjection;

namespace Observability.Infrastructure.Hosting;

public static class BackgroundServicesExtensions
{
    /// <summary>
    /// Registers the platform's scheduled background hosts — the nightly retention sweep (Issue 8.5)
    /// and the alert evaluator (Issue 8.3). Both wrap scoped services registered by
    /// <c>AddObservabilityInfrastructure</c>, and each self-gates on its <c>Enabled</c> option, so this
    /// is safe to call from any host (the API in deployment; the standalone Worker too). The services
    /// run only where their config enables them.
    /// </summary>
    public static IServiceCollection AddObservabilityBackgroundServices(this IServiceCollection services)
    {
        services.AddHostedService<RetentionSweepService>();
        services.AddHostedService<AlertEvaluationService>();
        return services;
    }
}
