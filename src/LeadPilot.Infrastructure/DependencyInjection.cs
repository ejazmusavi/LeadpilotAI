using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Infrastructure.Tenancy;
using LeadPilot.Infrastructure.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LeadPilot.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionName = "Default";

    /// <summary>Persistence, clock, e-mail, tenant context and job scope helper. Safe for every host (Web, Api, Worker).</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var cs = config.GetConnectionString(ConnectionName)
                 ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is not configured.");

        services.AddSingleton<TenantSaveChangesInterceptor>();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantContextAccessor>(sp => sp.GetRequiredService<TenantContext>());
        services.AddDbContext<AppDbContext>((sp, o) => o.UseMySQL(cs).AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>()));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ITenantScopeRunner, TenantScopeRunner>();

        var dp = services.AddDataProtection().SetApplicationName("LeadPilot");
        if (config["DataProtection:KeysPath"] is { Length: > 0 } keys) dp.PersistKeysToFileSystem(new DirectoryInfo(keys));
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        services.Configure<SmtpOptions>(config.GetSection(SmtpOptions.Section));
        if (env.IsDevelopment() && string.IsNullOrWhiteSpace(config[$"{SmtpOptions.Section}:Host"]))
            services.AddSingleton<IEmailSender, DevelopmentLogEmailSender>();
        else
            services.AddSingleton<IEmailSender, SmtpEmailSender>();

        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("mysql", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// Tenancy use cases and HTTP edge (user, request info, policies, spoof guard). For hosts that serve HTTP and register ASP.NET Identity (Web, Api).
    /// </summary>
    public static IServiceCollection AddTenancyServices(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<IRequestInfo, HttpRequestInfo>();
        services.AddScoped<IInvitationLinkBuilder, InvitationLinkBuilder>();

        services.AddScoped<IMembershipResolver, MembershipResolver>();
        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<InvitationIssuer>();
        services.AddScoped<ITenantAdminService, TenantAdminService>();
        services.AddScoped<ITenantSettingsService, TenantSettingsService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<IInvitationService, InvitationService>();
        services.AddScoped<ITenantSwitchService, TenantSwitchService>();
        services.AddScoped<IProfileService, ProfileService>();
        // Resolved lazily: only hosts that actually issue tokens (Api) need Jwt:SigningKey; the Api also resolves it eagerly at startup.
        services.AddSingleton(_ => JwtSettings.Resolve(config, env));
        services.AddScoped<IAuthTokenService, AuthTokenService>();

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<RejectSpoofedTenantFilter>();
        return services;
    }

}

public static class IApplicationBuilderExtensions
{
    /// <summary>Place after UseAuthentication and before UseAuthorization.</summary>
    public static Microsoft.AspNetCore.Builder.IApplicationBuilder UseTenantResolution(this Microsoft.AspNetCore.Builder.IApplicationBuilder app) =>
        app.UseMiddleware<TenantResolutionMiddleware>();
}
