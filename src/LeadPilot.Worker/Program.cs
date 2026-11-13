using Hangfire;
using LeadPilot.Worker;
using LeadPilot.Application;
using LeadPilot.Infrastructure;
using LeadPilot.Infrastructure.Discovery;
using LeadPilot.Infrastructure.Discovery.Jobs;
using LeadPilot.Infrastructure.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog(builder.Configuration, builder.Environment, "LeadPilot.Worker");
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
// no HTTP edge here: jobs run in an explicit tenant scope (ITenantScopeRunner) and fail closed without one
builder.Services.AddWorkerServices(builder.Configuration);
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
