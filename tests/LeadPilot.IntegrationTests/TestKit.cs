using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Captures e-mail instead of sending it (test double).</summary>
public sealed class EmailOutbox : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) { Sent.Enqueue(message); return Task.CompletedTask; }

    public string TokenFor(string to)
    {
        var mail = Sent.Reverse().First(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase));
        var match = Regex.Match(mail.HtmlBody, "token=([^\"&]+)");
        Assert.True(match.Success, "no token link in e-mail to " + to);
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }
}

public sealed record TestUser(Guid Id, string Email, string Password, Guid TenantId);

/// <summary>Two independent tenants with admins, read-only users and custom roles, built through the application's own services/DB.</summary>
public sealed class TestWorld
{
    public const string Password = "Wor1d!Passw0rd#";
    public TenantDto A { get; private set; } = default!;
    public TenantDto B { get; private set; } = default!;
    public TestUser AAdmin { get; private set; } = default!;
    public TestUser AReadOnly { get; private set; } = default!;
    public TestUser AUsersOnly { get; private set; } = default!;
    public TestUser BAdmin { get; private set; } = default!;
    public TestUser BReadOnly { get; private set; } = default!;
    public Guid RoleA { get; private set; }
    public Guid RoleB { get; private set; }
    public Guid TenantAdminRoleA { get; private set; }
    public Guid UsersOnlyRoleA { get; private set; }
    public Guid PlatformTenantId { get; private set; }
    public IServiceProvider Services { get; private set; } = default!;

    public static async Task<TestWorld> BuildAsync(IServiceProvider services)
    {
        var w = new TestWorld { Services = services };
        using var scope = services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<ITenantAdminService>();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        w.A = (await admin.CreateAsync(new CreateTenantRequest("Tenant A " + suffix, "tenant-a-" + suffix, "Education", "UTC", "en", null))).Value.Tenant;
        w.B = (await admin.CreateAsync(new CreateTenantRequest("Tenant B " + suffix, "tenant-b-" + suffix, "Retail", "UTC", "en", null))).Value.Tenant;

        w.AAdmin = await w.AddUserAsync(w.A.Id, $"a-admin-{suffix}@a.test", SystemRoles.TenantAdministrator);
        w.AReadOnly = await w.AddUserAsync(w.A.Id, $"a-ro-{suffix}@a.test", SystemRoles.ReadOnly);
        w.BAdmin = await w.AddUserAsync(w.B.Id, $"b-admin-{suffix}@b.test", SystemRoles.TenantAdministrator);
        w.BReadOnly = await w.AddUserAsync(w.B.Id, $"b-ro-{suffix}@b.test", SystemRoles.ReadOnly);

        w.RoleA = await w.AddRoleAsync(w.A.Id, "A Custom " + suffix, PermissionCodes.LeadsView);
        w.RoleB = await w.AddRoleAsync(w.B.Id, "B Custom " + suffix, PermissionCodes.LeadsView);
        w.UsersOnlyRoleA = await w.AddRoleAsync(w.A.Id, "Users only " + suffix, PermissionCodes.UsersManage);
        w.AUsersOnly = await w.AddUserAsync(w.A.Id, $"a-usersonly-{suffix}@a.test", "Users only " + suffix);
        w.TenantAdminRoleA = await w.RoleIdAsync(w.A.Id, SystemRoles.TenantAdministrator);
        w.PlatformTenantId = (await w.WithDb(db => db.Tenants.AsNoTracking().FirstAsync(t => t.IsPlatform))).Id;
        return w;
    }

    public async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        using var _ = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().BeginPlatformScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task WithDb(Func<AppDbContext, Task> work) => WithDb<int>(async db => { await work(db); return 0; });

    public async Task<Guid> RoleIdAsync(Guid tenantId, string name) =>
        await WithDb(db => db.TenantRoles.IgnoreQueryFilters().Where(r => r.TenantId == tenantId && r.Name == name).Select(r => r.Id).FirstAsync());

    public async Task<TestUser> AddUserAsync(Guid tenantId, string email, string roleName, string? password = null)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = email.Split('@')[0], CreatedAtUtc = DateTime.UtcNow };
        var created = await users.CreateAsync(user, password ?? Password);
        Assert.True(created.Succeeded, string.Join(";", created.Errors.Select(e => e.Description)));
        var roleId = await RoleIdAsync(tenantId, roleName);
        await WithDb(async db =>
        {
            db.UserTenants.Add(UserTenant.Create(tenantId, user.Id, true, null, DateTime.UtcNow));
            db.TenantUserRoles.Add(UserRole.Create(tenantId, user.Id, roleId, null, DateTime.UtcNow));
            await db.SaveChangesAsync();
        });
        return new TestUser(user.Id, email, password ?? Password, tenantId);
    }

    public async Task<Guid> AddRoleAsync(Guid tenantId, string name, params string[] permissionCodes)
    {
        return await WithDb(async db =>
        {
            var role = Role.Create(tenantId, name, null, false, DateTime.UtcNow);
            db.TenantRoles.Add(role);
            foreach (var p in await db.Permissions.Where(p => permissionCodes.Contains(p.Code)).ToListAsync())
                db.RolePermissions.Add(RolePermission.Create(tenantId, role.Id, p.Id));
            await db.SaveChangesAsync();
            return role.Id;
        });
    }
}

/// <summary>Bearer-token API client for one signed-in user.</summary>
public sealed class ApiSession
{
    public HttpClient Http { get; }
    public AuthTokens Tokens { get; private set; } = default!;
    private ApiSession(HttpClient http) => Http = http;

    public static async Task<ApiSession> LoginAsync(AppFixture app, string email, string password, Guid? tenantId = null)
    {
        var s = new ApiSession(app.Api!.CreateClient());
        var res = await s.Http.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, tenantId));
        Assert.True(res.IsSuccessStatusCode, $"login failed for {email}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        s.SetTokens((await res.Content.ReadFromJsonAsync<AuthTokens>())!);
        return s;
    }

    public void SetTokens(AuthTokens t)
    {
        Tokens = t;
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t.AccessToken);
    }

    public static ApiSession Anonymous(AppFixture app) => new(app.Api!.CreateClient());
    public static ApiSession WithRawToken(AppFixture app, string token)
    {
        var s = new ApiSession(app.Api!.CreateClient());
        s.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return s;
    }

    public Task<HttpResponseMessage> Get(string url) => Http.GetAsync(url);
    public Task<HttpResponseMessage> Post(string url, object? body = null) => Http.PostAsJsonAsync(url, body ?? new { });
    public Task<HttpResponseMessage> Put(string url, object body) => Http.PutAsJsonAsync(url, body);
    public Task<HttpResponseMessage> Delete(string url) => Http.DeleteAsync(url);

    public static async Task<T> Json<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    public static string TenantClaim(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.GetProperty("tid").GetString()!;
    }
}

/// <summary>Cookie-based MVC client for one signed-in user.</summary>
public sealed class WebSession
{
    public HttpClient Http { get; }
    private WebSession(HttpClient http) => Http = http;

    public static async Task<WebSession> LoginAsync(AppFixture app, string email, string password)
    {
        var s = new WebSession(app.NewWebClient());
        var token = await AppFixture.GetAntiForgeryTokenAsync(s.Http, "/Account/Login");
        var res = await s.Http.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Email"] = email, ["Password"] = password, ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return s;
    }

    public static WebSession Anonymous(AppFixture app) => new(app.NewWebClient());

    public Task<HttpResponseMessage> Get(string url) => Http.GetAsync(url);

    public async Task<string> Html(string url)
    {
        var r = await Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsStringAsync();
    }

    /// <summary>POST a form with a valid antiforgery token (taken from a page every signed-in user can open).</summary>
    public Task<HttpResponseMessage> Post(string url, params (string Key, string Value)[] fields) => PostWithToken("/Profile", url, fields);

    public async Task<HttpResponseMessage> PostWithToken(string tokenUrl, string url, params (string Key, string Value)[] fields)
    {
        var token = await AppFixture.GetAntiForgeryTokenAsync(Http, tokenUrl);
        var content = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).Append(new("__RequestVerificationToken", token));
        return await Http.PostAsync(url, new FormUrlEncodedContent(content));
    }
}
