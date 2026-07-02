using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Observability.Application.Alerting;

namespace Observability.Infrastructure.Hosting;

/// <summary>
/// Runs the alert evaluator on a fixed interval (<c>AlertEvaluatorOptions.EvaluationIntervalSeconds</c>,
/// Issue 8.3). The evaluation itself is a scoped service (<see cref="IAlertEvaluator"/>); this host just
/// ticks the schedule, opens a DI scope per pass, and keeps the loop alive across transient failures.
/// No-op when <c>AlertEvaluatorOptions.Enabled</c> is false (the default) — the evaluator polls the DB,
/// so it must stay off until a tenant is live, both to avoid waking a serverless DB and because there
/// is nothing to evaluate beforehand. Visibility-only until 8.4 notifications land.
/// </summary>
public sealed class AlertEvaluationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AlertEvaluationService> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;

    public AlertEvaluationService(
        IServiceScopeFactory scopeFactory,
        ILogger<AlertEvaluationService> logger,
        IOptions<AlertEvaluatorOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _enabled = options.Value.Enabled;
        var seconds = options.Value.EvaluationIntervalSeconds;
        _interval = TimeSpan.FromSeconds(seconds > 0 ? seconds : 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Alert evaluation is disabled (Observability:Alerting:Enabled=false); not scheduling.");
            return;
        }

        _logger.LogInformation("Alert evaluation running every {Interval}.", _interval);
        using var timer = new PeriodicTimer(_interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var evaluator = scope.ServiceProvider.GetRequiredService<IAlertEvaluator>();
                var result = await evaluator.EvaluateAsync(stoppingToken);
                if (result.AlertsFired > 0)
                    _logger.LogInformation(
                        "Alert evaluation fired {Fired} alert(s) across {Rules} rule(s).",
                        result.AlertsFired, result.RulesEvaluated);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Don't let one bad pass kill the schedule — log and wait for the next interval.
                _logger.LogError(ex, "Alert evaluation failed; will retry at the next interval.");
            }
        }
    }
}
