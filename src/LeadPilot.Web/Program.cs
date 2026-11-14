using FluentValidation;
using LeadPilot.Application;
using LeadPilot.Infrastructure;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Infrastructure.Logging;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Web;
using LeadPilot.Web.Services;
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

builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(o =>
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireDigit = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireUppercase = true;
        o.Password.RequireNonAlphanumeric = true;
        o.User.RequireUniqueEmail = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
        o.Tokens.PasswordResetTokenProvider = TokenOptions.DefaultProvider;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(2));
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));

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

builder.Services.AddControllersWithViews(o => o.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));

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
app.UseAuthorization();

app.MapStaticAssets();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}").WithStaticAssets();

await DatabaseInitializer.MigrateIfConfiguredAsync(app.Services, app.Configuration);
await IdentitySeeder.SeedInitialAdminAsync(app.Services, app.Configuration);

app.Run();

public partial class Program;
