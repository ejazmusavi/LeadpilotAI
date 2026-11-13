using FluentValidation;
using LeadPilot.Application;
using LeadPilot.Infrastructure;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Infrastructure.Logging;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Web;
using LeadPilot.Infrastructure.Seeding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseLeadPilotSerilog("LeadPilot.Web");

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(IdentitySetup.Configure)
    .AddEntityFrameworkStores<AppDbContext>()
    .AddClaimsPrincipalFactory<TenantClaimsPrincipalFactory>()
    .AddDefaultTokenProviders();
builder.Services.AddTenancyServices(builder.Configuration, builder.Environment);
builder.Services.Configure<TenantResolutionOptions>(o =>
{
    o.NoTenantRedirectPath = "/Account/NoWorkspace";
    o.SignOutOnAccessLoss = true;
    o.ExemptPathPrefixes = ["/Account", "/Tenants/Switch", "/Invitations/Accept", "/Home/Error", "/Home/StatusPage", "/health", "/css", "/js", "/lib", "/favicon"];
});

builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(2));
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(builder.Configuration.GetValue("Security:StampValidationMinutes", 5)));

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = ".LeadPilot.Auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/AccessDenied";
    // Real 403 (rendered by the status-code page) instead of a redirect: authorization failures are visible to clients and tests.
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    // The security-stamp validator rebuilds the principal periodically; keep the user's active-tenant choice across that refresh.
    var inner = o.Events.OnValidatePrincipal;
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var before = ctx.Principal;
        var tid = before?.FindFirst(TenantClaims.TenantId)?.Value;
        await inner(ctx);
        if (tid is not null && ctx.Principal is not null && !ReferenceEquals(ctx.Principal, before) && ctx.Principal.Identity is System.Security.Claims.ClaimsIdentity id && Guid.TryParse(tid, out var g))
            id.ReplaceTenantClaim(g);
    };
});

// Authentication policy: everything requires a signed-in user unless explicitly [AllowAnonymous].
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitLimit", 20), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();   // trusted proxy is the private Nginx container; tighten per deployment
    o.KnownProxies.Clear();
});

builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
    o.Filters.AddService<RejectSpoofedTenantFilter>();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment()) app.UseDeveloperExceptionPage();
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/Home/StatusPage", "?code={0}");
app.UseSerilogRequestLoggingSafe();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseTenantResolution();
app.UseAuthorization();

app.MapStaticAssets();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}").WithStaticAssets();

await DatabaseInitializer.MigrateIfConfiguredAsync(app.Services, app.Configuration);
await PlatformSeeder.SeedAsync(app.Services, app.Configuration);

app.Run();

public partial class Program;
