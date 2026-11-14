using LeadPilot.Application.Agents;
using LeadPilot.Application.Conversion;
using LeadPilot.Infrastructure.Messaging.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LeadPilot.Infrastructure.Conversion;

public static class ConversionServiceCollection
{
    /// <summary>
    /// Demos, trials, customers and payments. Registers the REAL implementations of the Phase 8 ports (IDemoScheduler, ITrialProvisioner, IPaymentRequestGateway). Payment provider adapters are NOT registered here:
    /// no provider has been chosen, so a webhook can never verify anything until an adapter is added deliberately.
    /// </summary>
    public static IServiceCollection AddConversion(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ConversionOptions>(config.GetSection(ConversionOptions.Section));
        services.AddScoped<IConversionSettingsService, ConversionSettingsService>();
        services.AddScoped<IDemoService, DemoService>();
        services.AddScoped<IDemoReminderService, DemoReminderService>();
        services.AddScoped<ITrialService, TrialService>();
        services.AddScoped<ITrialLifecycleService, TrialLifecycleService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<PaymentVerificationCore>();
        services.AddScoped<PaymentService>();
        services.AddScoped<IPaymentService>(sp => sp.GetRequiredService<PaymentService>());
        services.AddScoped<IPaymentProviderService, PaymentProviderService>();
        services.AddScoped<IPaymentWebhookService, PaymentWebhookService>();
        services.AddScoped<PaymentWebhookDirectory>();
        services.AddScoped<ConversionDirectory>();
        services.AddScoped<ConversionSweepJob>();
        services.TryAddScoped<IMeetingLinkProvider, TemplateMeetingLinkProvider>();
        services.TryAddScoped<ITrialProvisioningBackend, ManualTrialProvisioningBackend>();
        services.TryAddScoped<IServiceNoticeSender, ServiceNoticeSender>();
        services.AddScoped<IDemoScheduler, DemoSchedulerPort>();
        services.AddScoped<ITrialProvisioner, TrialProvisionerPort>();
        services.AddScoped<IPaymentRequestGateway, PaymentRequestGateway>();
        return services;
    }
}
