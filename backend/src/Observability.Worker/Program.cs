using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Observability.Infrastructure;
using Observability.Infrastructure.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Shares the Infrastructure DI registrations (DbContext, RetentionOptions, IRetentionSweeper,
// AlertEvaluatorOptions, IAlertEvaluator) with the API so behavior is identical across hosts.
builder.Services.AddObservabilityInfrastructure(builder.Configuration);

// Phase 8.5 nightly retention sweep + Phase 8.3 alert evaluator. These now also run in the API host
// (the deployed process); the standalone Worker remains a valid host for running them on their own.
// Each self-gates on its Enabled option.
builder.Services.AddObservabilityBackgroundServices();

var host = builder.Build();
await host.RunAsync();
