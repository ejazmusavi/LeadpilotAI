using System.Net;
using System.Net.Http.Json;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Common;
using LeadPilot.Application.Security;
using LeadPilot.Infrastructure.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Tenant isolation, authentication (401) and permission (403) semantics of the REST API.</summary>
[Collection("app")]
public class ApiTenancyTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static readonly string[] ProtectedGets = ["/api/v1/roles", "/api/v1/users", "/api/v1/invitations", "/api/v1/settings", "/api/v1/settings/profile", "/api/v1/audit-logs", "/api/v1/tenants", "/api/v1/permissions"];

    [RequiresMySqlFact]
    public async Task Unauthenticated_requests_return_401_everywhere()
    {
        var anon = ApiSession.Anonymous(app);
        foreach (var url in ProtectedGets) Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/v1/roles", new { name = "x", permissions = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Delete($"/api/v1/roles/{W.RoleA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/v1/auth/switch-tenant", new { tenantId = W.A.Id })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Login_rejects_bad_credentials_with_401_and_issues_tenant_bound_tokens_on_success()
    {
        var anon = ApiSession.Anonymous(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/v1/auth/login", new { email = W.AAdmin.Email, password = "Wrong!Passw0rd#1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/v1/auth/login", new { email = "ghost@nowhere.test", password = "Wrong!Passw0rd#1" })).StatusCode);

        var s = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        Assert.Equal(W.A.Id.ToString(), ApiSession.TenantClaim(s.Tokens.AccessToken));
        Assert.Equal(W.A.Id, s.Tokens.TenantId);
        Assert.False(string.IsNullOrEmpty(s.Tokens.RefreshToken));
    }

    [RequiresMySqlFact]
    public async Task Forged_expired_and_unauthorized_tid_tokens_are_rejected()
    {
        string Mint(Dictionary<string, object> claims, DateTime? expires = null) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "leadpilot", Audience = "leadpilot-api", Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            NotBefore = (expires ?? DateTime.UtcNow.AddMinutes(10)).AddHours(-1), IssuedAt = (expires ?? DateTime.UtcNow.AddMinutes(10)).AddHours(-1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSettings.DevelopmentOnlyKey)), SecurityAlgorithms.HmacSha256),
            Claims = claims,
        });

        // wrong signing key
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "leadpilot", Audience = "leadpilot-api", Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('x', 48))), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object> { ["sub"] = W.AAdmin.Id.ToString(), ["tid"] = W.A.Id.ToString() },
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiSession.WithRawToken(app, forged).Get("/api/v1/roles")).StatusCode);

        // correctly signed but expired
        var expired = Mint(new() { ["sub"] = W.AAdmin.Id.ToString(), ["tid"] = W.A.Id.ToString() }, DateTime.UtcNow.AddMinutes(-30));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiSession.WithRawToken(app, expired).Get("/api/v1/roles")).StatusCode);

        // correctly signed, but names a tenant the user does not belong to: the claim is only a request
        var wrongTenant = Mint(new() { ["sub"] = W.AAdmin.Id.ToString(), ["tid"] = W.B.Id.ToString() });
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiSession.WithRawToken(app, wrongTenant).Get("/api/v1/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiSession.WithRawToken(app, Mint(new() { ["sub"] = W.AAdmin.Id.ToString(), ["tid"] = Guid.NewGuid().ToString() })).Get("/api/v1/roles")).StatusCode);
        // no tenant claim at all: fail closed
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiSession.WithRawToken(app, Mint(new() { ["sub"] = W.AAdmin.Id.ToString() })).Get("/api/v1/roles")).StatusCode);
        // unknown user
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiSession.WithRawToken(app, Mint(new() { ["sub"] = Guid.NewGuid().ToString(), ["tid"] = W.A.Id.ToString() })).Get("/api/v1/roles")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Missing_permission_returns_403_while_permitted_users_succeed()
    {
        var ro = await ApiSession.LoginAsync(app, W.AReadOnly.Email, W.AReadOnly.Password);
        foreach (var url in new[] { "/api/v1/roles", "/api/v1/users", "/api/v1/invitations", "/api/v1/settings", "/api/v1/audit-logs", "/api/v1/tenants", "/api/v1/permissions" })
            Assert.Equal(HttpStatusCode.Forbidden, (await ro.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/api/v1/roles", new { name = "nope", permissions = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Delete($"/api/v1/roles/{W.RoleA}")).StatusCode);

        var admin = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        foreach (var url in new[] { "/api/v1/roles", "/api/v1/users", "/api/v1/invitations", "/api/v1/settings", "/api/v1/audit-logs", "/api/v1/permissions" })
            Assert.Equal(HttpStatusCode.OK, (await admin.Get(url)).StatusCode);
        // tenant admins are not platform operators
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Get("/api/v1/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Get($"/api/v1/tenants/{W.B.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Post($"/api/v1/tenants/{W.B.Id}/suspend", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Post("/api/v1/tenants", new { name = "Rogue" })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Tenant_A_cannot_list_get_update_or_delete_tenant_B_roles()
    {
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var b = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);

        var listA = await ApiSession.Json<List<RoleDto>>(await a.Get("/api/v1/roles"));
        var listB = await ApiSession.Json<List<RoleDto>>(await b.Get("/api/v1/roles"));
        Assert.Contains(listA, r => r.Id == W.RoleA);
        Assert.DoesNotContain(listA, r => r.Id == W.RoleB);
        Assert.Empty(listA.Select(r => r.Id).Intersect(listB.Select(r => r.Id))); // even the system roles are separate rows per tenant

        Assert.Equal(HttpStatusCode.NotFound, (await a.Get($"/api/v1/roles/{W.RoleB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Put($"/api/v1/roles/{W.RoleB}", new SaveRoleRequest("hijacked", null, [PermissionCodes.LeadsView]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Delete($"/api/v1/roles/{W.RoleB}")).StatusCode);

        var still = await ApiSession.Json<RoleDetailDto>(await b.Get($"/api/v1/roles/{W.RoleB}"));
        Assert.StartsWith("B Custom", still.Name);
    }

    [RequiresMySqlFact]
    public async Task Tenant_A_cannot_see_or_change_tenant_B_users_or_invitations()
    {
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var b = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);

        var users = await ApiSession.Json<PagedResult<MemberDto>>(await a.Get("/api/v1/users?pageSize=100"));
        Assert.DoesNotContain(users.Items, u => u.UserId == W.BAdmin.Id || u.UserId == W.BReadOnly.Id);
        Assert.Contains(users.Items, u => u.UserId == W.AAdmin.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await a.Get($"/api/v1/users/{W.BReadOnly.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Put($"/api/v1/users/{W.BReadOnly.Id}/roles", new { roleIds = new[] { W.TenantAdminRoleA } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Put($"/api/v1/users/{W.BReadOnly.Id}/status", new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Delete($"/api/v1/users/{W.BReadOnly.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Get($"/api/v1/users/{W.BReadOnly.Id}")).StatusCode);

        var invite = await b.Post("/api/v1/invitations", new { email = $"iso-{Guid.NewGuid():N}@b.test", roleId = W.RoleB });
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        var inv = await ApiSession.Json<InvitationDto>(invite);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Delete($"/api/v1/invitations/{inv.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post($"/api/v1/invitations/{inv.Id}/resend")).StatusCode);
        var aInvites = await ApiSession.Json<PagedResult<InvitationDto>>(await a.Get("/api/v1/invitations?pageSize=100"));
        Assert.DoesNotContain(aInvites.Items, i => i.Id == inv.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await b.Delete($"/api/v1/invitations/{inv.Id}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Another_tenants_role_cannot_be_used_for_assignment_or_invitation()
    {
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Put($"/api/v1/users/{W.AReadOnly.Id}/roles", new { roleIds = new[] { W.RoleB } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/invitations", new { email = $"x-{Guid.NewGuid():N}@a.test", roleId = W.RoleB })).StatusCode);
        var ro = await ApiSession.Json<MemberDto>(await a.Get($"/api/v1/users/{W.AReadOnly.Id}"));
        Assert.DoesNotContain(ro.Roles, r => r.Id == W.RoleB);
    }

    [RequiresMySqlFact]
    public async Task Spoofed_tenant_id_in_header_query_or_body_is_rejected()
    {
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var b = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);

        async Task<HttpStatusCode> WithHeader(string value)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/roles");
            req.Headers.Add("X-Tenant-Id", value);
            return (await a.Http.SendAsync(req)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.BadRequest, await WithHeader(W.B.Id.ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, await WithHeader("not-a-guid"));
        Assert.Equal(HttpStatusCode.OK, await WithHeader(W.A.Id.ToString())); // repeating the authorized tenant is harmless

        Assert.Equal(HttpStatusCode.BadRequest, (await a.Get($"/api/v1/roles?tenantId={W.B.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Get($"/api/v1/users?tenantId={W.B.Id}")).StatusCode);

        var name = "Spoof " + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/roles", new { name, permissions = new[] { "Leads.View" }, tenantId = W.B.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Put($"/api/v1/roles/{W.RoleA}", new { name, permissions = new[] { "Leads.View" }, tenantId = W.B.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Put("/api/v1/settings/spoof.key", new { value = "v", isSecret = false, tenantId = W.B.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/invitations", new { email = $"s-{Guid.NewGuid():N}@a.test", roleId = W.RoleA, tenantId = W.B.Id })).StatusCode);

        var bRoles = await ApiSession.Json<List<RoleDto>>(await b.Get("/api/v1/roles"));
        var aRoles = await ApiSession.Json<List<RoleDto>>(await a.Get("/api/v1/roles"));
        Assert.DoesNotContain(bRoles.Concat(aRoles), r => r.Name == name);

        // same tenant in the body is fine and still lands in the authorized tenant
        var ok = await a.Post("/api/v1/roles", new { name, permissions = new[] { "Leads.View" }, tenantId = W.A.Id });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Delete($"/api/v1/roles/{(await ApiSession.Json<RoleDetailDto>(ok)).Id}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Two_tenants_operate_independently_with_the_same_names_and_settings()
    {
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var b = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);
        var shared = "Shared Role " + Guid.NewGuid().ToString("N")[..6];

        var ra = await a.Post("/api/v1/roles", new { name = shared, permissions = new[] { "Leads.View" } });
        var rb = await b.Post("/api/v1/roles", new { name = shared, permissions = new[] { "Campaigns.View", "Reports.View" } });
        Assert.Equal(HttpStatusCode.Created, ra.StatusCode);
        Assert.Equal(HttpStatusCode.Created, rb.StatusCode); // (TenantId, Name) is unique per tenant, not globally
        var idA = (await ApiSession.Json<RoleDetailDto>(ra)).Id;
        var idB = (await ApiSession.Json<RoleDetailDto>(rb)).Id;
        Assert.Equal(HttpStatusCode.Conflict, (await a.Post("/api/v1/roles", new { name = shared, permissions = Array.Empty<string>() })).StatusCode);

        Assert.Single((await ApiSession.Json<RoleDetailDto>(await a.Get($"/api/v1/roles/{idA}"))).Permissions);
        Assert.Equal(2, (await ApiSession.Json<RoleDetailDto>(await b.Get($"/api/v1/roles/{idB}"))).Permissions.Count);

        var key = "app.greeting";
        Assert.Equal(HttpStatusCode.OK, (await a.Put($"/api/v1/settings/{key}", new { value = "hello from A", isSecret = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Put($"/api/v1/settings/{key}", new { value = "hello from B", isSecret = false })).StatusCode);
        var sa = await ApiSession.Json<List<TenantSettingDto>>(await a.Get("/api/v1/settings"));
        var sb = await ApiSession.Json<List<TenantSettingDto>>(await b.Get("/api/v1/settings"));
        Assert.Equal("hello from A", sa.Single(s => s.Key == key).Value);
        Assert.Equal("hello from B", sb.Single(s => s.Key == key).Value);

        // A deleting "its" key never touches B's
        Assert.Equal(HttpStatusCode.NoContent, (await a.Delete($"/api/v1/settings/{key}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Delete($"/api/v1/settings/{key}")).StatusCode);
        Assert.Contains(await ApiSession.Json<List<TenantSettingDto>>(await b.Get("/api/v1/settings")), s => s.Key == key);
        await b.Delete($"/api/v1/settings/{key}");
        await a.Delete($"/api/v1/roles/{idA}");
        await b.Delete($"/api/v1/roles/{idB}");
    }

    [RequiresMySqlFact]
    public async Task Secret_settings_are_masked_in_responses_and_encrypted_at_rest()
    {
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var res = await a.Put("/api/v1/settings/gateway.apiKey", new { value = "super-secret-key-123", isSecret = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain("super-secret-key-123", await res.Content.ReadAsStringAsync());
        var list = await a.Get("/api/v1/settings");
        Assert.DoesNotContain("super-secret-key-123", await list.Content.ReadAsStringAsync());

        var stored = await W.WithDb(db => db.TenantSettings.IgnoreQueryFilters().Where(s => s.TenantId == W.A.Id && s.Key == "gateway.apiKey").Select(s => s.Value).FirstAsync());
        Assert.StartsWith("enc:v1:", stored);
        Assert.DoesNotContain("super-secret-key-123", stored);
        await a.Delete("/api/v1/settings/gateway.apiKey");
    }

    [RequiresMySqlFact]
    public async Task Roles_follow_the_rules_system_roles_are_immutable_and_roles_in_use_cannot_be_deleted()
    {
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Put($"/api/v1/roles/{W.TenantAdminRoleA}", new SaveRoleRequest("renamed", null, [PermissionCodes.LeadsView]))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Delete($"/api/v1/roles/{W.TenantAdminRoleA}")).StatusCode);

        var created = await ApiSession.Json<RoleDetailDto>(await a.Post("/api/v1/roles", new { name = "Temp " + Guid.NewGuid().ToString("N")[..6], permissions = new[] { "Leads.View" } }));
        Assert.Equal(HttpStatusCode.OK, (await a.Put($"/api/v1/users/{W.AReadOnly.Id}/roles", new { roleIds = new[] { created.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Delete($"/api/v1/roles/{created.Id}")).StatusCode);
        var readOnlyRoleId = await W.RoleIdAsync(W.A.Id, SystemRoles.ReadOnly);
        Assert.Equal(HttpStatusCode.OK, (await a.Put($"/api/v1/users/{W.AReadOnly.Id}/roles", new { roleIds = new[] { readOnlyRoleId } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Delete($"/api/v1/roles/{created.Id}")).StatusCode);

        // platform permissions cannot be placed on a customer-tenant role; unknown codes are rejected
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/roles", new { name = "Sneaky", permissions = new[] { PermissionCodes.TenantsManage } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/roles", new { name = "Bogus", permissions = new[] { "Everything.Do" } })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Users_cannot_escalate_privileges_beyond_their_own_permissions()
    {
        var u = await ApiSession.LoginAsync(app, W.AUsersOnly.Email, W.AUsersOnly.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await u.Post("/api/v1/roles", new { name = "Grab settings", permissions = new[] { PermissionCodes.SettingsManage } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await u.Post("/api/v1/invitations", new { email = $"e-{Guid.NewGuid():N}@a.test", roleId = W.TenantAdminRoleA })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await u.Put($"/api/v1/users/{W.AReadOnly.Id}/roles", new { roleIds = new[] { W.TenantAdminRoleA } })).StatusCode);
        // changing roles that carry only permissions they hold is allowed
        Assert.Equal(HttpStatusCode.OK, (await u.Get("/api/v1/users")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task The_last_administrator_and_the_acting_user_cannot_be_removed_or_disabled()
    {
        // A dedicated tenant with exactly one administrator and one other user, so the shared world is never touched.
        var suffix = Guid.NewGuid().ToString("N")[..6];
        using var scope = W.Services.CreateScope();
        var tenant = (await scope.ServiceProvider.GetRequiredService<ITenantAdminService>()
            .CreateAsync(new CreateTenantRequest("Solo " + suffix, "solo-" + suffix, null, null, null, null))).Value.Tenant;
        var admin = await W.AddUserAsync(tenant.Id, $"solo-admin-{suffix}@solo.test", SystemRoles.TenantAdministrator);
        var staff = await W.AddUserAsync(tenant.Id, $"solo-staff-{suffix}@solo.test", SystemRoles.TenantStaff);
        var a = await ApiSession.LoginAsync(app, admin.Email, admin.Password);

        Assert.Equal(HttpStatusCode.Conflict, (await a.Delete($"/api/v1/users/{admin.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Put($"/api/v1/users/{admin.Id}/status", new { active = false })).StatusCode);
        // stripping the only Users.Manage holder's admin role is refused too
        var staffRole = await W.RoleIdAsync(tenant.Id, SystemRoles.TenantStaff);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Put($"/api/v1/users/{admin.Id}/roles", new { roleIds = new[] { staffRole } })).StatusCode);
        // other users can still be managed
        Assert.Equal(HttpStatusCode.NoContent, (await a.Delete($"/api/v1/users/{staff.Id}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Disabled_member_loses_access_immediately_and_can_be_re_enabled()
    {
        var u = await W.AddUserAsync(W.A.Id, $"temp-{Guid.NewGuid():N}@a.test", SystemRoles.ReadOnly);
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var victim = await ApiSession.LoginAsync(app, u.Email, u.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await victim.Get("/api/v1/roles")).StatusCode); // authenticated, no permission → 403 (not 401)

        Assert.Equal(HttpStatusCode.NoContent, (await a.Put($"/api/v1/users/{u.Id}/status", new { active = false })).StatusCode);
        var denied = await victim.Get("/api/v1/permissions");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiSession.Anonymous(app).Post("/api/v1/auth/login", new { email = u.Email, password = u.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Put($"/api/v1/users/{u.Id}/status", new { active = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await victim.Get("/api/v1/roles")).StatusCode); // member again, still without Users.Manage
        Assert.Equal(HttpStatusCode.NoContent, (await a.Delete($"/api/v1/users/{u.Id}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_every_session()
    {
        var s = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var first = s.Tokens;
        var anon = ApiSession.Anonymous(app);

        var rotated = await anon.Post("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = await ApiSession.Json<AuthTokens>(rotated);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // replaying the already-rotated token is treated as theft: it fails and the newer token dies too
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/v1/auth/refresh", new { refreshToken = second.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/v1/auth/refresh", new { refreshToken = "garbage" })).StatusCode);

        var stored = await W.WithDb(db => db.RefreshTokens.AnyAsync(t => t.TokenHash == first.RefreshToken));
        Assert.False(stored); // only hashes are stored
    }

    [RequiresMySqlFact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var s = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post("/api/v1/auth/logout", new { refreshToken = s.Tokens.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiSession.Anonymous(app).Post("/api/v1/auth/refresh", new { refreshToken = s.Tokens.RefreshToken })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Suspended_tenant_is_blocked_everywhere_in_the_api_and_recovers_on_activation()
    {
        var po = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var b = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);
        Assert.Equal(HttpStatusCode.OK, (await b.Get("/api/v1/roles")).StatusCode);
        try
        {
            var suspend = await po.Post($"/api/v1/tenants/{W.B.Id}/suspend", new { reason = "non-payment" });
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
            Assert.Equal("Suspended", (await ApiSession.Json<TenantDto>(suspend)).Status);

            var blocked = await b.Get("/api/v1/roles");
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Contains("tenant_suspended", await blocked.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await b.Get("/api/v1/users")).StatusCode);
            // refresh tokens were revoked on suspension: never a success
            Assert.NotEqual(HttpStatusCode.OK, (await ApiSession.Anonymous(app).Post("/api/v1/auth/refresh", new { refreshToken = b.Tokens.RefreshToken })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ApiSession.Anonymous(app).Post("/api/v1/auth/login", new { email = W.BAdmin.Email, password = W.BAdmin.Password })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await b.Post("/api/v1/roles", new { name = "while-suspended", permissions = Array.Empty<string>() })).StatusCode);

            // other tenants are unaffected; platform tenant cannot be suspended
            Assert.Equal(HttpStatusCode.OK, (await a.Get("/api/v1/roles")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await po.Post($"/api/v1/tenants/{W.PlatformTenantId}/suspend", new { reason = "oops" })).StatusCode);
            // not even platform users may switch into a suspended tenant
            Assert.Equal(HttpStatusCode.Forbidden, (await po.Post("/api/v1/auth/switch-tenant", new { tenantId = W.B.Id })).StatusCode);
        }
        finally
        {
            Assert.Equal(HttpStatusCode.OK, (await po.Post($"/api/v1/tenants/{W.B.Id}/activate")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await b.Get("/api/v1/roles")).StatusCode); // same access token works again
        var audit = await ApiSession.Json<PagedResult<AuditLogDto>>(await b.Get("/api/v1/audit-logs?action=Tenant.&pageSize=50"));
        Assert.Contains(audit.Items, e => e.Action == "Tenant.Suspended");
        Assert.Contains(audit.Items, e => e.Action == "Tenant.Activated");
    }

    [RequiresMySqlFact]
    public async Task Platform_tenant_switch_is_audited_and_ordinary_users_cannot_switch_into_other_tenants()
    {
        var po = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        Assert.NotEqual(W.A.Id.ToString(), ApiSession.TenantClaim(po.Tokens.AccessToken));

        var switched = await po.Post("/api/v1/auth/switch-tenant", new { tenantId = W.A.Id });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        var tokens = await ApiSession.Json<AuthTokens>(switched);
        Assert.Equal(W.A.Id.ToString(), ApiSession.TenantClaim(tokens.AccessToken));
        po.SetTokens(tokens);

        // acting inside A: sees A's data (and only A's)
        var roles = await ApiSession.Json<List<RoleDto>>(await po.Get("/api/v1/roles"));
        Assert.Contains(roles, r => r.Id == W.RoleA);
        Assert.DoesNotContain(roles, r => r.Id == W.RoleB);
        Assert.Equal(HttpStatusCode.NotFound, (await po.Get($"/api/v1/roles/{W.RoleB}")).StatusCode);
        // platform roles are not offered inside a customer tenant
        var catalog = await ApiSession.Json<List<PermissionDto>>(await po.Get("/api/v1/permissions"));
        Assert.DoesNotContain(catalog, p => p.IsPlatform);

        // A's own admin can see the switch in A's audit trail, including who did it
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var audit = await ApiSession.Json<PagedResult<AuditLogDto>>(await a.Get("/api/v1/audit-logs?action=Tenant.Switched&pageSize=50"));
        Assert.Contains(audit.Items, e => e.ActorEmail == AppFixture.AdminEmail && e.Summary.Contains("platform access"));

        // tenant users cannot switch into someone else's tenant, nor learn whether it exists; nothing is audited in B
        var before = (await ApiSession.Json<PagedResult<AuditLogDto>>(await (await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password)).Get("/api/v1/audit-logs?action=Tenant.Switched&pageSize=50"))).TotalCount;
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post("/api/v1/auth/switch-tenant", new { tenantId = W.B.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post("/api/v1/auth/switch-tenant", new { tenantId = Guid.NewGuid() })).StatusCode);
        var after = (await ApiSession.Json<PagedResult<AuditLogDto>>(await (await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password)).Get("/api/v1/audit-logs?action=Tenant.Switched&pageSize=50"))).TotalCount;
        Assert.Equal(before, after);
    }

    [RequiresMySqlFact]
    public async Task Platform_users_signing_in_directly_to_a_tenant_are_audited_and_ordinary_users_cannot()
    {
        var po = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword, W.A.Id);
        Assert.Equal(W.A.Id, po.Tokens.TenantId);
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var audit = await ApiSession.Json<PagedResult<AuditLogDto>>(await a.Get("/api/v1/audit-logs?action=Tenant.Switched&pageSize=50"));
        Assert.Contains(audit.Items, e => e.ActorEmail == AppFixture.AdminEmail && e.Summary.Contains("signed into"));

        // a tenant user asking for someone else's tenant at login gets nothing
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiSession.Anonymous(app).Post("/api/v1/auth/login", new { email = W.AAdmin.Email, password = W.AAdmin.Password, tenantId = W.B.Id })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Platform_owner_manages_tenants_and_new_tenants_get_the_default_roles()
    {
        var po = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var slug = "api-" + Guid.NewGuid().ToString("N")[..8];
        var created = await po.Post("/api/v1/tenants", new { name = "API Created " + slug, slug, industry = "Test" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tenant = (await ApiSession.Json<CreateTenantResult>(created)).Tenant;

        Assert.Equal(HttpStatusCode.Conflict, (await po.Post("/api/v1/tenants", new { name = "Dup", slug })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await po.Post("/api/v1/tenants", new { name = "Bad", slug = "Bad Slug!" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await po.Post("/api/v1/tenants", new { name = "Reserved", slug = "admin" })).StatusCode);

        var roleNames = await W.WithDb(db => db.TenantRoles.IgnoreQueryFilters().Where(r => r.TenantId == tenant.Id && r.IsSystem).Select(r => r.Name).ToListAsync());
        Assert.Equal(new[] { SystemRoles.MarketingManager, SystemRoles.ReadOnly, SystemRoles.SalesAgent, SystemRoles.SalesManager, SystemRoles.TenantAdministrator, SystemRoles.TenantStaff }.Order(), roleNames.Order());

        var updated = await po.Put($"/api/v1/tenants/{tenant.Id}", new { name = "Renamed " + slug, industry = "Other", timezone = "Asia/Karachi", locale = "en-GB" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Asia/Karachi", (await ApiSession.Json<TenantDto>(updated)).Timezone);
        Assert.Equal(HttpStatusCode.NotFound, (await po.Get($"/api/v1/tenants/{Guid.NewGuid()}")).StatusCode);
        var list = await ApiSession.Json<PagedResult<TenantDto>>(await po.Get($"/api/v1/tenants?search={slug}"));
        Assert.Single(list.Items);
    }

    [RequiresMySqlFact]
    public async Task Settings_profile_updates_only_the_callers_tenant()
    {
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var before = await ApiSession.Json<TenantDto>(await a.Get("/api/v1/settings/profile"));
        Assert.Equal(W.A.Id, before.Id);
        var res = await a.Put("/api/v1/settings/profile", new { name = before.Name, industry = "Updated", timezone = "UTC", locale = "en" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var b = await W.WithDb(db => db.Tenants.AsNoTracking().FirstAsync(t => t.Id == W.B.Id));
        Assert.NotEqual("Updated", b.Industry);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Put("/api/v1/settings/profile", new { name = "", timezone = "UTC", locale = "en" })).StatusCode);
    }
}
