using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Observability.Application.Retention;

namespace Observability.Worker;

/// <summary>
/// Runs the retention sweep once per day at <c>RetentionOptions.DailyRunAtUtc</c> (Issue 8.5). The
/// sweep itself is a scoped service (<see cref="IRetentionSweeper"/>); this host just schedules it,
/// opens a DI scope per run, and keeps the loop alive across transient failures.
/// </summary>
public sealed class RetentionSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionSweepService> _logger;
    private readonly TimeSpan _runAtUtc;

    public RetentionSweepService(
        IServiceScopeFactory scopeFactory,
        ILogger<RetentionSweepService> logger,
        IOptions<RetentionOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _runAtUtc = TimeSpan.TryParse(options.Value.DailyRunAtUtc, out var t) ? t : new TimeSpan(3, 0, 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Catch up on a missed run: if today's scheduled slot already passed and no sweep has run since
        // then, a restart after the daily time would otherwise skip the day entirely. Run once now.
        if (await ShouldCatchUpAsync(DateTime.UtcNow, stoppingToken))
        {
            _logger.LogInformation("Today's retention slot already passed with no completed sweep; running catch-up now.");
            await RunSweepAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayUntilNextRun(DateTime.UtcNow);
            _logger.LogInformation("Next retention sweep in {Delay} (at {Time:hh\\:mm} UTC).", delay, _runAtUtc);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunSweepAsync(stoppingToken);
        }
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sweeper = scope.ServiceProvider.GetRequiredService<IRetentionSweeper>();
            var result = await sweeper.SweepAsync(stoppingToken);
            _logger.LogInformation(
                "Retention sweep complete: {Events} events, {Errors} errors, {Audit} audit rows across {Envs} environments.",
                result.EventsDeleted, result.ErrorsDeleted, result.AuditLogsDeleted, result.EnvironmentsSwept);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down — let the loop observe cancellation and exit.
        }
        catch (Exception ex)
        {
            // Don't let one bad night kill the schedule — log and wait for the next window.
            _logger.LogError(ex, "Retention sweep failed; will retry at the next scheduled run.");
        }
    }

    /// <summary>
    /// True when the daily slot for <paramref name="nowUtc"/> has already elapsed and no sweep has run
    /// on or after that slot — i.e. the run for today was missed (e.g. the process started after the slot).
    /// </summary>
    private async Task<bool> ShouldCatchUpAsync(DateTime nowUtc, CancellationToken ct)
    {
        var todayRun = DateTime.SpecifyKind(nowUtc.Date + _runAtUtc, DateTimeKind.Utc);
        if (nowUtc < todayRun) return false;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sweeper = scope.ServiceProvider.GetRequiredService<IRetentionSweeper>();
            var lastSweep = await sweeper.GetLastSweepAtUtcAsync(ct);
            return lastSweep is null || lastSweep < todayRun;
        }
        catch (Exception ex)
        {
            // If we can't determine the last run, don't block startup or risk a double-run; fall through
            // to the normal schedule.
            _logger.LogWarning(ex, "Could not determine last retention sweep time; skipping catch-up check.");
            return false;
        }
    }

    private TimeSpan DelayUntilNextRun(DateTime nowUtc)
    {
        var todayRun = DateTime.SpecifyKind(nowUtc.Date + _runAtUtc, DateTimeKind.Utc);
        var next = nowUtc < todayRun ? todayRun : todayRun.AddDays(1);
        return next - nowUtc;
    }
}
