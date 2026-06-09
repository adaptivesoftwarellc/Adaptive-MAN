using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Observability.Infrastructure;
using Observability.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Phase 8.5 — nightly retention sweep. Shares the Infrastructure DI registrations (DbContext,
// RetentionOptions, IRetentionSweeper) with the API so behavior is identical across hosts.
builder.Services.AddObservabilityInfrastructure(builder.Configuration);
builder.Services.AddHostedService<RetentionSweepService>();

// Phase 8.3 — alert rule engine. Evaluates AlertRules on an interval and persists fired alerts
// (visibility-only until 8.4 notifications land).
builder.Services.AddHostedService<AlertEvaluationService>();

var host = builder.Build();
await host.RunAsync();
