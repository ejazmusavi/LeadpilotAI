using System.Net;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.IntegrationTests;

/// <summary>Tenant isolation, authentication and permission enforcement of the MVC dashboard (server-side, not just hidden menus).</summary>
[Collection("app")]
public class WebTenancyTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static readonly string[] AdminPages = ["/Users", "/Roles", "/Roles/Create", "/Settings", "/Audit", "/Invitations", "/Invitations/Create"];
    private static readonly string[] PlatformPages = ["/Tenants", "/Tenants/Create"];

    [RequiresMySqlFact]
    public async Task Anonymous_users_are_redirected_to_login_for_every_tenant_page()
    {
        var anon = WebSession.Anonymous(app);
        foreach (var url in AdminPages.Concat(PlatformPages).Concat(["/Tenants/Switch", "/Profile", "/"]))
        {
            var r = await anon.Get(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/Login", r.Headers.Location!.OriginalString);
        }
    }

    [RequiresMySqlFact]
    public async Task Missing_permission_returns_403_for_pages_and_posts()
    {
        var ro = await WebSession.LoginAsync(app, W.AReadOnly.Email, W.AReadOnly.Password);
        Assert.Equal(HttpStatusCode.OK, (await ro.Get("/")).StatusCode);
        foreach (var url in AdminPages.Concat(PlatformPages)) Assert.Equal(HttpStatusCode.Forbidden, (await ro.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post($"/Roles/Delete/{W.RoleA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post($"/Users/Remove/{W.AUsersOnly.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/Settings/Save", ("Key", "x"), ("Value", "y"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/Roles/Create", ("Name", "Sneaky"), ("Permissions", "Leads.View"))).StatusCode);
        Assert.True(await W.WithDb(db => db.TenantRoles.IgnoreQueryFilters().AnyAsync(r => r.Id == W.RoleA)));

        var admin = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        foreach (var url in AdminPages) Assert.Equal(HttpStatusCode.OK, (await admin.Get(url)).StatusCode);
        foreach (var url in PlatformPages) Assert.Equal(HttpStatusCode.Forbidden, (await admin.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Post($"/Tenants/Suspend/{W.B.Id}", ("Reason", "x"))).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Post_without_antiforgery_token_is_rejected_even_for_admins()
    {
        var admin = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var res = await admin.Http.PostAsync($"/Roles/Delete/{W.RoleA}", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True(await W.WithDb(db => db.TenantRoles.IgnoreQueryFilters().AnyAsync(r => r.Id == W.RoleA)));
    }

    [RequiresMySqlFact]
    public async Task Sidebar_only_lists_pages_the_user_may_open_and_all_listed_pages_exist()
    {
        var ro = await WebSession.LoginAsync(app, W.AReadOnly.Email, W.AReadOnly.Password);
        var roHtml = await ro.Html("/");
        Assert.DoesNotContain("href=\"/Users\"", roHtml);
        Assert.DoesNotContain("href=\"/Roles\"", roHtml);
        Assert.DoesNotContain("href=\"/Tenants\"", roHtml);

        var admin = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var html = await admin.Html("/");
        foreach (var link in new[] { "/Users", "/Invitations", "/Roles", "/Settings", "/Audit" }) Assert.Contains($"href=\"{link}\"", html);
        Assert.DoesNotContain("href=\"/Tenants\"", html);
        Assert.Contains(W.A.Name, html); // active tenant is always visible

        var po = await WebSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        Assert.Contains("href=\"/Tenants\"", await po.Html("/"));
    }

    [RequiresMySqlFact]
    public async Task Tenant_A_cannot_read_or_modify_tenant_B_through_the_dashboard()
    {
        var a = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var bRoleName = await W.WithDb(db => db.TenantRoles.IgnoreQueryFilters().Where(r => r.Id == W.RoleB).Select(r => r.Name).FirstAsync());
        var aRoleName = await W.WithDb(db => db.TenantRoles.IgnoreQueryFilters().Where(r => r.Id == W.RoleA).Select(r => r.Name).FirstAsync());

        var list = await a.Html("/Roles");
        Assert.Contains(aRoleName, list);
        Assert.DoesNotContain(bRoleName, list);
        var users = await a.Html("/Users?q=" + Uri.EscapeDataString("@b.test"));
        Assert.DoesNotContain(W.BAdmin.Email, users);
        Assert.DoesNotContain(W.BReadOnly.Email, await a.Html("/Users"));

        Assert.Equal(HttpStatusCode.NotFound, (await a.Get($"/Roles/Edit/{W.RoleB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post($"/Roles/Edit/{W.RoleB}", ("Name", "Hijacked"), ("Permissions", "Leads.View"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post($"/Roles/Delete/{W.RoleB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Get($"/Users/Edit/{W.BReadOnly.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post($"/Users/Edit/{W.BReadOnly.Id}", ("selectedRoleIds", W.TenantAdminRoleA.ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post($"/Users/SetActive/{W.BReadOnly.Id}", ("active", "false"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post($"/Users/Remove/{W.BReadOnly.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post($"/Invitations/Revoke/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post($"/Settings/Delete", ("key", "does.not.exist"))).StatusCode);

        // B is untouched
        var b = await W.WithDb(async db => new
        {
            Role = await db.TenantRoles.IgnoreQueryFilters().Where(r => r.Id == W.RoleB).Select(r => r.Name).FirstAsync(),
            Member = await db.UserTenants.IgnoreQueryFilters().AnyAsync(m => m.TenantId == W.B.Id && m.UserId == W.BReadOnly.Id && m.Status == LeadPilot.Domain.Tenancy.MembershipStatus.Active),
        });
        Assert.Equal(bRoleName, b.Role);
        Assert.True(b.Member);
    }

    [RequiresMySqlFact]
    public async Task Spoofed_tenant_id_in_forms_or_query_is_rejected()
    {
        var a = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Get($"/Roles?tenantId={W.B.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Get($"/Users?tenantId={W.B.Id}")).StatusCode);
        var name = "Spoof " + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/Roles/Create", ("Name", name), ("Permissions", "Leads.View"), ("TenantId", W.B.Id.ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/Settings/Save", ("Key", "spoof"), ("Value", "v"), ("TenantId", W.B.Id.ToString()))).StatusCode);
        Assert.False(await W.WithDb(db => db.TenantRoles.IgnoreQueryFilters().AnyAsync(r => r.Name == name)));

        var own = await a.Post("/Roles/Create", ("Name", name), ("Permissions", "Leads.View"), ("TenantId", W.A.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, own.StatusCode);
        var created = await W.WithDb(db => db.TenantRoles.IgnoreQueryFilters().SingleAsync(r => r.Name == name));
        Assert.Equal(W.A.Id, created.TenantId);
        Assert.Equal(HttpStatusCode.Redirect, (await a.Post($"/Roles/Delete/{created.Id}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Roles_users_and_settings_can_be_managed_through_the_dashboard()
    {
        var a = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var name = "UI Role " + Guid.NewGuid().ToString("N")[..6];

        // roles
        Assert.Equal(HttpStatusCode.Redirect, (await a.Post("/Roles/Create", ("Name", name), ("Description", "made in test"), ("Permissions", "Leads.View"), ("Permissions", "Reports.View"))).StatusCode);
        var roleId = await W.WithDb(db => db.TenantRoles.IgnoreQueryFilters().Where(r => r.TenantId == W.A.Id && r.Name == name).Select(r => r.Id).SingleAsync());
        var page = await a.Html($"/Roles/Edit/{roleId}");
        Assert.Contains("value=\"Reports.View\"", page);
        var dup = await a.Post("/Roles/Create", ("Name", name), ("Permissions", "Leads.View"));
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.Contains("already exists", await dup.Content.ReadAsStringAsync());
        var sysEdit = await a.Html($"/Roles/Edit/{W.TenantAdminRoleA}");
        Assert.Contains("System roles are fixed", sysEdit);
        Assert.Equal(HttpStatusCode.OK, (await a.Post($"/Roles/Edit/{W.TenantAdminRoleA}", ("Name", "x"), ("Permissions", "Leads.View"))).StatusCode); // re-rendered with the error
        Assert.Contains("System roles cannot be modified", await (await a.Post($"/Roles/Edit/{W.TenantAdminRoleA}", ("Name", "x"), ("Permissions", "Leads.View"))).Content.ReadAsStringAsync());

        // role assignment
        Assert.Equal(HttpStatusCode.Redirect, (await a.Post($"/Users/Edit/{W.AReadOnly.Id}", ("selectedRoleIds", roleId.ToString()))).StatusCode);
        Assert.Contains(name, await a.Html("/Users"));
        var readOnlyRole = await W.RoleIdAsync(W.A.Id, SystemRoles.ReadOnly);
        Assert.Equal(HttpStatusCode.Redirect, (await a.Post($"/Users/Edit/{W.AReadOnly.Id}", ("selectedRoleIds", readOnlyRole.ToString()))).StatusCode);

        // destructive actions come with confirmation dialogs
        var usersHtml = await a.Html("/Users");
        Assert.Contains("data-confirm=\"Remove ", usersHtml);
        Assert.Contains("data-confirm=\"Delete the role", await a.Html("/Roles"));

        Assert.Equal(HttpStatusCode.Redirect, (await a.Post($"/Roles/Delete/{roleId}")).StatusCode);
        Assert.DoesNotContain(name, await a.Html("/Roles"));

        // settings: profile + key/value + secret masking
        Assert.Equal(HttpStatusCode.Redirect, (await a.Post("/Settings/Profile", ("Name", W.A.Name), ("Industry", "Education"), ("Timezone", "Asia/Karachi"), ("Locale", "en"))).StatusCode);
        Assert.Contains("Asia/Karachi", await a.Html("/Settings"));
        Assert.Equal(HttpStatusCode.Redirect, (await a.Post("/Settings/Save", ("Key", "webhook.secret"), ("Value", "s3cr3t-value"), ("IsSecret", "true"), ("IsSecret", "false"))).StatusCode);
        var html = await a.Html("/Settings");
        Assert.Contains("webhook.secret", html);
        Assert.DoesNotContain("s3cr3t-value", html);
        Assert.Equal(HttpStatusCode.Redirect, (await a.Post("/Settings/Delete", ("key", "webhook.secret"))).StatusCode);
        var bad = await a.Post("/Settings/Profile", ("Name", ""), ("Timezone", "UTC"), ("Locale", "en"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);

        // audit trail records what happened, inside this tenant only
        var audit = await a.Html("/Audit?q=Role.");
        Assert.Contains("Role.Created", audit);
        Assert.Contains("Role.Deleted", audit);
    }

    [RequiresMySqlFact]
    public async Task Users_can_edit_their_own_profile()
    {
        var u = await W.AddUserAsync(W.A.Id, $"profile-{Guid.NewGuid():N}@a.test", SystemRoles.ReadOnly);
        var s = await WebSession.LoginAsync(app, u.Email, u.Password);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Profile", ("DisplayName", "Renamed Person"), ("Phone", "+92 300 1234567"))).StatusCode);
        var html = await s.Html("/Profile");
        Assert.Contains("Renamed Person", html);
        Assert.Contains("+92 300 1234567", System.Net.WebUtility.HtmlDecode(html));
        Assert.Equal(HttpStatusCode.OK, (await s.Post("/Profile", ("DisplayName", ""))).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Platform_owner_manages_tenants_through_the_dashboard()
    {
        var po = await WebSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var slug = "ui-" + Guid.NewGuid().ToString("N")[..8];
        var create = await po.Post("/Tenants/Create", ("Name", "UI Tenant " + slug), ("Slug", slug), ("Industry", "Test"), ("Timezone", "UTC"), ("Locale", "en"), ("AdminEmail", $"owner-{slug}@ui.test"));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var id = await W.WithDb(db => db.Tenants.Where(t => t.Slug == slug).Select(t => t.Id).SingleAsync());
        Assert.Contains("UI Tenant " + slug, await po.Html($"/Tenants/Details/{id}"));
        Assert.Contains(slug, await po.Html("/Tenants?q=" + slug));
        // the administrator invitation went out as a real e-mail with a working link
        Assert.False(string.IsNullOrEmpty(app.Outbox.TokenFor($"owner-{slug}@ui.test")));

        var dup = await po.Post("/Tenants/Create", ("Name", "Another"), ("Slug", slug), ("Timezone", "UTC"), ("Locale", "en"));
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.Contains("already in use", await dup.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Redirect, (await po.Post($"/Tenants/Suspend/{id}", ("Reason", "testing"))).StatusCode);
        Assert.Contains("Suspended", await po.Html($"/Tenants/Details/{id}"));
        Assert.Equal(HttpStatusCode.Redirect, (await po.Post($"/Tenants/Activate/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await po.Get($"/Tenants/Details/{Guid.NewGuid()}")).StatusCode);
        Assert.Contains("data-confirm=", await po.Html($"/Tenants/Details/{id}")); // switch action is confirmed
    }

    [RequiresMySqlFact]
    public async Task Suspended_tenant_blocks_dashboard_sessions_and_new_sign_ins()
    {
        var po = await WebSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var b = await WebSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);
        Assert.Equal(HttpStatusCode.OK, (await b.Get("/Roles")).StatusCode);
        try
        {
            Assert.Equal(HttpStatusCode.Redirect, (await po.Post($"/Tenants/Suspend/{W.B.Id}", ("Reason", "billing"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await b.Get("/Roles")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await b.Get("/")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await b.PostWithToken("/Tenants/Switch", $"/Roles/Delete/{W.RoleB}")).StatusCode);

            // a fresh sign-in has no usable workspace
            var fresh = await WebSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);
            var home = await fresh.Get("/");
            Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
            Assert.EndsWith("/Account/NoWorkspace", home.Headers.Location!.OriginalString);

            // other tenants carry on; platform users cannot enter a suspended tenant
            Assert.Equal(HttpStatusCode.OK, (await (await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password)).Get("/Roles")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await po.Post($"/Tenants/Switch/{W.B.Id}")).StatusCode); // refused with a message, nothing switched
            Assert.Equal(HttpStatusCode.OK, (await po.Get("/Tenants")).StatusCode);
        }
        finally
        {
            Assert.Equal(HttpStatusCode.Redirect, (await po.Post($"/Tenants/Activate/{W.B.Id}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await b.Get("/Roles")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Tenant_switching_is_authorized_and_audited()
    {
        var po = await WebSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var switchPage = await po.Html("/Tenants/Switch");
        Assert.Contains(W.A.Name, switchPage);
        Assert.Contains(W.B.Name, switchPage);

        Assert.Equal(HttpStatusCode.Redirect, (await po.Post($"/Tenants/Switch/{W.A.Id}")).StatusCode);
        var home = await po.Html("/");
        Assert.Contains(W.A.Name, home);
        Assert.Contains("platform access", home);

        var aRoleName = await W.WithDb(db => db.TenantRoles.IgnoreQueryFilters().Where(r => r.Id == W.RoleA).Select(r => r.Name).FirstAsync());
        var bRoleName = await W.WithDb(db => db.TenantRoles.IgnoreQueryFilters().Where(r => r.Id == W.RoleB).Select(r => r.Name).FirstAsync());
        var roles = await po.Html("/Roles");
        Assert.Contains(aRoleName, roles);
        Assert.DoesNotContain(bRoleName, roles);
        Assert.Equal(HttpStatusCode.NotFound, (await po.Get($"/Roles/Edit/{W.RoleB}")).StatusCode);
        Assert.Contains("Tenant.Switched", await po.Html("/Audit?q=Tenant.Switched"));

        Assert.Equal(HttpStatusCode.Redirect, (await po.Post($"/Tenants/Switch/{W.PlatformTenantId}")).StatusCode);
        Assert.DoesNotContain("platform access", await po.Html("/"));

        // ordinary users: only their own tenants; others look like they do not exist
        var a = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var aSwitch = await a.Html("/Tenants/Switch");
        Assert.Contains(W.A.Name, aSwitch);
        Assert.DoesNotContain(W.B.Name, aSwitch);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post($"/Tenants/Switch/{W.B.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post($"/Tenants/Switch/{Guid.NewGuid()}")).StatusCode);
        Assert.Contains(W.A.Name, await a.Html("/")); // still in A
    }

    [RequiresMySqlFact]
    public async Task Sign_in_is_audited_in_the_users_workspace()
    {
        await WebSession.LoginAsync(app, W.AReadOnly.Email, W.AReadOnly.Password);
        var a = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var audit = await a.Html("/Audit?q=Auth.SignedIn");
        Assert.Contains(W.AReadOnly.Email, audit);
        Assert.DoesNotContain(W.BAdmin.Email, audit);
    }
}
