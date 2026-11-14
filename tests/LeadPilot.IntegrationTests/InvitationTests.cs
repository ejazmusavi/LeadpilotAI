using System.Net;
using System.Net.Http.Json;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class InvitationTests(AppFixture app)
{
    private TestWorld W => app.World;
    private const string StrongPassword = "Inv1te!Passw0rd#";

    private async Task<(InvitationDto Invitation, string Token)> InviteAsync(ApiSession admin, string email, Guid roleId)
    {
        var res = await admin.Post("/api/v1/invitations", new { email, roleId });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ApiSession.Json<InvitationDto>(res), app.Outbox.TokenFor(email));
    }

    private static Task<HttpResponseMessage> Accept(WebSession s, string token, string name = "Invited Person", string password = StrongPassword, string? confirm = null) =>
        s.PostWithToken("/Account/Login", "/Invitations/Accept",
            ("Token", token), ("DisplayName", name), ("Password", password), ("ConfirmPassword", confirm ?? password));

    [RequiresMySqlFact]
    public async Task Invitation_flow_creates_the_account_in_the_inviting_tenant_only_and_works_once()
    {
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var email = $"new-{Guid.NewGuid():N}@invite.test";
        var (inv, token) = await InviteAsync(a, email, W.RoleA);
        Assert.Equal("Pending", inv.Status);
        Assert.True(inv.ExpiresAtUtc > DateTime.UtcNow.AddDays(6));

        // the raw token is never stored, only its SHA-256 hash
        var rows = await W.WithDb(db => db.TenantInvitations.IgnoreQueryFilters().Where(i => i.Id == inv.Id).Select(i => i.TokenHash).ToListAsync());
        Assert.Equal(64, rows.Single().Length);
        Assert.NotEqual(token, rows.Single());
        Assert.False(await W.WithDb(db => db.TenantInvitations.IgnoreQueryFilters().AnyAsync(i => i.TokenHash == token)));

        var invitee = WebSession.Anonymous(app);
        var landing = await invitee.Html($"/Invitations/Accept?token={Uri.EscapeDataString(token)}");
        Assert.Contains(W.A.Name, landing);
        Assert.Contains(email, landing);

        // weak password / mismatching confirmation do not consume the invitation
        Assert.Equal(HttpStatusCode.OK, (await Accept(invitee, token, password: "short")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Accept(invitee, token, confirm: "Different!Passw0rd#1")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Accept(invitee, token)).StatusCode);

        // signed in straight into tenant A, with the invited role
        var home = await invitee.Html("/");
        Assert.Contains(W.A.Name, home);
        var members = await ApiSession.Json<PagedResult<MemberDto>>(await a.Get($"/api/v1/users?search={Uri.EscapeDataString(email)}"));
        var member = Assert.Single(members.Items);
        Assert.Equal(W.RoleA, Assert.Single(member.Roles).Id);
        var b = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);
        Assert.Empty((await ApiSession.Json<PagedResult<MemberDto>>(await b.Get($"/api/v1/users?search={Uri.EscapeDataString(email)}"))).Items);

        // single use
        Assert.Equal(HttpStatusCode.BadRequest, (await Accept(WebSession.Anonymous(app), token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await WebSession.Anonymous(app).Get($"/Invitations/Accept?token={Uri.EscapeDataString(token)}")).StatusCode);
        var list = await ApiSession.Json<PagedResult<InvitationDto>>(await a.Get("/api/v1/invitations?pageSize=100"));
        Assert.Equal("Accepted", list.Items.Single(i => i.Id == inv.Id).Status);
        var audit = await ApiSession.Json<PagedResult<AuditLogDto>>(await a.Get("/api/v1/audit-logs?action=Invitation.&pageSize=50"));
        Assert.Contains(audit.Items, e => e.Action == "Invitation.Accepted" && e.Summary.Contains(email));

        // the new user really lives in tenant A and cannot reach B's data
        var newUser = await ApiSession.LoginAsync(app, email, StrongPassword);
        Assert.Equal(W.A.Id, newUser.Tokens.TenantId);
        Assert.Equal(HttpStatusCode.Forbidden, (await newUser.Get("/api/v1/roles")).StatusCode); // Custom role only has Leads.View
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiSession.Anonymous(app).Post("/api/v1/auth/login", new { email, password = StrongPassword, tenantId = W.B.Id })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Expired_revoked_unknown_and_replaced_tokens_are_all_rejected()
    {
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var anon = WebSession.Anonymous(app);

        // expired
        var (expired, expiredToken) = await InviteAsync(a, $"exp-{Guid.NewGuid():N}@invite.test", W.RoleA);
        await W.WithDb(db => db.TenantInvitations.IgnoreQueryFilters().Where(i => i.Id == expired.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1))));
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Get($"/Invitations/Accept?token={Uri.EscapeDataString(expiredToken)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Accept(anon, expiredToken)).StatusCode);
        Assert.Equal("Expired", (await ApiSession.Json<PagedResult<InvitationDto>>(await a.Get("/api/v1/invitations?pageSize=100"))).Items.Single(i => i.Id == expired.Id).Status);

        // revoked
        var (revoked, revokedToken) = await InviteAsync(a, $"rev-{Guid.NewGuid():N}@invite.test", W.RoleA);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Delete($"/api/v1/invitations/{revoked.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Accept(anon, revokedToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Delete($"/api/v1/invitations/{revoked.Id}")).StatusCode);

        // resend replaces the token: the old link dies, the new one works
        var email = $"resend-{Guid.NewGuid():N}@invite.test";
        var (pending, oldToken) = await InviteAsync(a, email, W.RoleA);
        Assert.Equal(HttpStatusCode.OK, (await a.Post($"/api/v1/invitations/{pending.Id}/resend")).StatusCode);
        var newToken = app.Outbox.TokenFor(email);
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Get($"/Invitations/Accept?token={Uri.EscapeDataString(oldToken)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.Get($"/Invitations/Accept?token={Uri.EscapeDataString(newToken)}")).StatusCode);

        // inviting the same address again supersedes the earlier invitation
        var (_, newest) = await InviteAsync(a, email, W.RoleA);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Get($"/Invitations/Accept?token={Uri.EscapeDataString(newToken)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.Get($"/Invitations/Accept?token={Uri.EscapeDataString(newest)}")).StatusCode);

        // garbage
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Get("/Invitations/Accept?token=nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Get("/Invitations/Accept")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiSession.Anonymous(app).Post("/api/v1/invitations/accept", new { token = "nonsense", displayName = "x", password = StrongPassword })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Existing_users_must_sign_in_as_the_invitee_and_can_then_belong_to_several_tenants()
    {
        var multi = await W.AddUserAsync(W.B.Id, $"multi-{Guid.NewGuid():N}@b.test", SystemRoles.ReadOnly);
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var (_, token) = await InviteAsync(a, multi.Email, W.RoleA);

        var accept = new { token, displayName = "Ignored", password = StrongPassword };
        // anonymous, or signed in as somebody else: refused
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiSession.Anonymous(app).Post("/api/v1/invitations/accept", accept)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.Post("/api/v1/invitations/accept", accept)).StatusCode);

        var me = await ApiSession.LoginAsync(app, multi.Email, multi.Password); // default tenant = B
        Assert.Equal(W.B.Id, me.Tokens.TenantId);
        Assert.Equal(HttpStatusCode.OK, (await me.Post("/api/v1/invitations/accept", accept)).StatusCode);

        // still defaults to B; can switch into A (audited) and is a plain member there with only the invited role
        Assert.Equal(W.B.Id, (await ApiSession.LoginAsync(app, multi.Email, multi.Password)).Tokens.TenantId);
        var switched = await me.Post("/api/v1/auth/switch-tenant", new { tenantId = W.A.Id });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        me.SetTokens(await ApiSession.Json<AuthTokens>(switched));
        Assert.Equal(W.A.Id.ToString(), ApiSession.TenantClaim(me.Tokens.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, (await me.Get("/api/v1/roles")).StatusCode); // Leads.View only
        var members = await ApiSession.Json<PagedResult<MemberDto>>(await a.Get($"/api/v1/users?search={Uri.EscapeDataString(multi.Email)}"));
        Assert.Equal(W.RoleA, Assert.Single(Assert.Single(members.Items).Roles).Id);
        // the same person is not duplicated into a tenant they were not invited to
        var bAdmin = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);
        var bMembers = await ApiSession.Json<PagedResult<MemberDto>>(await bAdmin.Get($"/api/v1/users?search={Uri.EscapeDataString(multi.Email)}"));
        Assert.DoesNotContain(Assert.Single(bMembers.Items).Roles, r => r.Id == W.RoleA);
    }

    [RequiresMySqlFact]
    public async Task Inviting_an_existing_member_or_using_a_disabled_workspace_is_refused()
    {
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Post("/api/v1/invitations", new { email = W.AReadOnly.Email, roleId = W.RoleA })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/invitations", new { email = "not-an-email", roleId = W.RoleA })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/invitations", new { email = "x@invite.test", roleId = Guid.Empty })).StatusCode);

        // an invitation to a suspended tenant cannot be accepted
        var po = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var created = await ApiSession.Json<CreateTenantResult>(await po.Post("/api/v1/tenants", new { name = "Inv " + suffix, slug = "inv-" + suffix, adminEmail = $"adm-{suffix}@invite.test" }));
        Assert.True(created.AdminInvited);
        var token = app.Outbox.TokenFor($"adm-{suffix}@invite.test");
        Assert.Equal(HttpStatusCode.OK, (await po.Post($"/api/v1/tenants/{created.Tenant.Id}/suspend", new { reason = "t" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await WebSession.Anonymous(app).Get($"/Invitations/Accept?token={Uri.EscapeDataString(token)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiSession.Anonymous(app).Post("/api/v1/invitations/accept", new { token, displayName = "Admin", password = StrongPassword })).StatusCode);
        await po.Post($"/api/v1/tenants/{created.Tenant.Id}/activate");
        // after activation the tenant administrator invitation works and grants the Tenant Administrator role
        var res = await ApiSession.Anonymous(app).Post("/api/v1/invitations/accept", new { token, displayName = "Tenant Admin", password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var admin = await ApiSession.LoginAsync(app, $"adm-{suffix}@invite.test", StrongPassword);
        Assert.Equal(created.Tenant.Id, admin.Tokens.TenantId);
        Assert.Equal(HttpStatusCode.OK, (await admin.Get("/api/v1/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Get("/api/v1/tenants")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Invitations_are_managed_through_the_dashboard()
    {
        var a = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var email = $"ui-{Guid.NewGuid():N}@invite.test";
        var create = await a.Post("/Invitations/Create", ("Email", email), ("RoleId", W.RoleA.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var page = await a.Html("/Invitations");
        Assert.Contains(email, page);
        Assert.Contains("data-confirm=\"Revoke", page);

        var bad = await a.Post("/Invitations/Create", ("Email", email), ("RoleId", W.RoleB.ToString()));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode); // form re-rendered: other tenant's role is not valid here
        Assert.Contains("role does not exist", await bad.Content.ReadAsStringAsync());

        var id = await W.WithDb(db => db.TenantInvitations.IgnoreQueryFilters().Where(i => i.Email == email).Select(i => i.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await a.Post($"/Invitations/Resend/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await a.Post($"/Invitations/Revoke/{id}")).StatusCode);
        Assert.Equal(InvitationStatus.Revoked, await W.WithDb(db => db.TenantInvitations.IgnoreQueryFilters().Where(i => i.Id == id).Select(i => i.Status).SingleAsync()));
    }
}
