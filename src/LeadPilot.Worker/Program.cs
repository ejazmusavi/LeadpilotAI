using LeadPilot.Worker;
using LeadPilot.Application;
using LeadPilot.Infrastructure;
using LeadPilot.Infrastructure.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog(builder.Configuration, builder.Environment, "LeadPilot.Worker");
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
