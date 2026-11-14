using LeadPilot.Application.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LeadPilot.Infrastructure.Workflows;

public static class WorkflowServiceCollection
{
    /// <summary>The workflow engine: services, actions, the trigger service, the consumer handler and the Hangfire jobs. Needs CRM, messaging and agents (Phase 10 ports) registered.</summary>
    public static IServiceCollection AddWorkflows(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<WorkflowOptions>(config.GetSection(WorkflowOptions.Section));
        services.AddScoped<WorkflowSupport>();
        services.AddScoped<WorkflowDirectory>();
        services.AddScoped<IWorkflowService, WorkflowService>();
        services.AddScoped<IWorkflowExecutionService, WorkflowExecutionService>();
        services.AddScoped<IWorkflowSimulationService, WorkflowSimulationService>();
        services.AddScoped<IWorkflowSettingsService, WorkflowSettingsService>();
        services.AddScoped<IWorkflowNotificationService, WorkflowNotificationService>();
        services.AddScoped<IWorkflowTriggerService, WorkflowTriggerService>();
        services.AddScoped<IWorkflowEngine, WorkflowEngine>();
        services.AddScoped<IWorkflowAction, AssignLeadAction>();
        services.AddScoped<IWorkflowAction, ChangeLeadStatusAction>();
        services.AddScoped<IWorkflowAction, ChangeLeadStageAction>();
        services.AddScoped<IWorkflowAction, SendMessageAction>();
        services.AddScoped<IWorkflowAction, CreateFollowUpAction>();
        services.AddScoped<IWorkflowAction, NotifyUserAction>();
        services.AddScoped<IWorkflowAction, ScheduleDemoAction>();
        services.AddScoped<IWorkflowAction, EscalateToHumanAction>();
        services.AddScoped<IWorkflowAction, UpdateLeadFieldsAction>();
        services.AddScoped<IWorkflowAction, AddTagAction>();
        services.AddScoped<LeadPilot.Application.Messaging.IMessagingEventHandler, WorkflowEventHandler>();
        services.TryAddSingleton<IWorkflowJobScheduler, HangfireWorkflowScheduler>();
        services.AddScoped<WorkflowJob>();
        services.AddScoped<WorkflowSweepJob>();
        return services;
    }
}
