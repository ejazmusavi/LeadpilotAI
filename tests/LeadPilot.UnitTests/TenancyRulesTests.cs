using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Security;
using LeadPilot.Application.Tenancy;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Infrastructure.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LeadPilot.UnitTests;

public class PermissionEvaluationTests
{
    [Fact]
    public void Effective_permissions_are_the_union_of_role_permissions()
    {
        var set = PermissionSet.Union([[PermissionCodes.LeadsView], [PermissionCodes.LeadsView, PermissionCodes.ReportsView], []]);
        Assert.True(set.Has(PermissionCodes.LeadsView));
        Assert.True(set.Has(PermissionCodes.ReportsView));
        Assert.False(set.Has(PermissionCodes.UsersManage));
        Assert.Equal(2, set.Codes.Count);
    }

    [Fact]
    public void Empty_set_grants_nothing_and_codes_are_case_sensitive()
    {
        Assert.False(PermissionSet.Empty.Has(PermissionCodes.LeadsView));
        Assert.False(new PermissionSet([PermissionCodes.LeadsView]).Has("leads.view"));
        Assert.True(PermissionSet.Empty.HasAll([]));
    }

    [Fact]
    public void Escalation_guard_lists_permissions_the_actor_does_not_hold()
    {
        var actor = new PermissionSet([PermissionCodes.UsersManage, PermissionCodes.LeadsView]);
        Assert.Empty(actor.Ungrantable([PermissionCodes.LeadsView]));
        Assert.Equal([PermissionCodes.SettingsManage], actor.Ungrantable([PermissionCodes.LeadsView, PermissionCodes.SettingsManage, PermissionCodes.SettingsManage]));
    }

    [Fact]
    public void Platform_and_unknown_permissions_are_invalid_on_customer_tenant_roles()
    {
        Assert.Empty(PermissionSet.InvalidFor([PermissionCodes.LeadsView], roleIsInPlatformTenant: false));
        Assert.Equal([PermissionCodes.TenantsManage], PermissionSet.InvalidFor([PermissionCodes.TenantsManage], roleIsInPlatformTenant: false));
        Assert.Empty(PermissionSet.InvalidFor([PermissionCodes.TenantsManage], roleIsInPlatformTenant: true));
        Assert.Equal(["Nope.Nothing"], PermissionSet.InvalidFor(["Nope.Nothing"], roleIsInPlatformTenant: true));
    }

    [Fact]
    public void Catalog_has_the_required_codes_unique()
    {
        var codes = PermissionCatalog.All.Select(p => p.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        foreach (var required in new[] { "Leads.View", "Leads.Create", "Leads.Update", "Leads.Delete", "Campaigns.View", "Campaigns.Create", "Campaigns.Execute",
                     "Conversations.View", "Conversations.Reply", "Agents.Manage", "Reports.View", "Users.Manage", "Settings.Manage", "Tenants.Manage" })
            Assert.Contains(required, codes);
        Assert.Contains(PermissionCatalog.All, p => p.IsPlatform && p.Code.StartsWith("Platform."));
    }

    [Fact]
    public void Default_roles_only_reference_catalog_permissions_and_platform_permissions_stay_on_platform_roles()
    {
        foreach (var role in SystemRoles.All)
        {
            Assert.All(role.Permissions, c => Assert.True(PermissionCatalog.Exists(c), $"{role.Name}: {c}"));
            Assert.Equal(role.Permissions.Count, role.Permissions.Distinct().Count());
            if (!role.PlatformOnly) Assert.DoesNotContain(role.Permissions, PermissionCatalog.IsPlatform);
        }
        Assert.Equal(8, SystemRoles.All.Count);
        Assert.Equal(PermissionCatalog.All.Count, SystemRoles.All.Single(r => r.Name == SystemRoles.PlatformOwner).Permissions.Count);
        Assert.Equal(2, SystemRoles.For(platformTenant: true).Count());
        Assert.Equal(6, SystemRoles.For(platformTenant: false).Count());
    }

    [Fact]
    public void Read_only_and_sales_agent_cannot_change_administration_or_delete()
    {
        var ro = SystemRoles.All.Single(r => r.Name == SystemRoles.ReadOnly).Permissions;
        Assert.DoesNotContain(ro, c => c.EndsWith(".Create") || c.EndsWith(".Update") || c.EndsWith(".Delete") || c.EndsWith(".Execute") || c.EndsWith(".Reply") || c.EndsWith(".Manage"));
        var agent = SystemRoles.All.Single(r => r.Name == SystemRoles.SalesAgent).Permissions;
        Assert.DoesNotContain(PermissionCodes.LeadsDelete, agent);
        Assert.DoesNotContain(PermissionCodes.UsersManage, agent);
        var admin = SystemRoles.All.Single(r => r.Name == SystemRoles.TenantAdministrator).Permissions;
        Assert.Contains(PermissionCodes.UsersManage, admin);
        Assert.DoesNotContain(PermissionCodes.TenantsManage, admin);
        var saas = SystemRoles.All.Single(r => r.Name == SystemRoles.SaasAdministrator).Permissions;
        Assert.Contains(PermissionCodes.PlatformSwitchTenant, saas);
        Assert.DoesNotContain(PermissionCodes.LeadsDelete, saas); // support staff are read-only on customer business data
    }
}

public class TenantRulesTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("acme", true)] [InlineData("acme-corp-2", true)] [InlineData("a1b", true)]
    [InlineData("ab", false)] [InlineData("-acme", false)] [InlineData("acme-", false)] [InlineData("Acme", false)] [InlineData("acme corp", false)] [InlineData("acme_corp", false)] [InlineData("", false)]
    public void Slug_validation(string slug, bool valid) => Assert.Equal(valid, Tenant.IsValidSlug(slug));

    [Fact]
    public void Slug_is_derived_and_normalised_from_names()
    {
        Assert.Equal("acme-ltd", Tenant.SlugFromName("Acme Ltd."));
        Assert.Equal("a-b-c", Tenant.SlugFromName("  A / B & C  "));
        Assert.Equal("acme", Tenant.NormalizeSlug("  ACME "));
        Assert.True(Tenant.SlugFromName(new string('x', 200)).Length <= 63);
    }

    [Fact]
    public void New_tenants_are_active_and_validate_their_profile()
    {
        var t = Tenant.Create("  Acme  ", "ACME-CO", "Retail", "Asia/Karachi", "en-GB", Now);
        Assert.Equal("Acme", t.Name);
        Assert.Equal("acme-co", t.Slug);
        Assert.Equal(TenantStatus.Active, t.Status);
        Assert.False(t.IsPlatform);
        Assert.Throws<DomainException>(() => Tenant.Create("", "acme", null, null, null, Now));
        Assert.Throws<DomainException>(() => Tenant.Create("Acme", "bad slug", null, null, null, Now));
        Assert.Throws<DomainException>(() => Tenant.Create("Acme", "acme", null, "Not A Zone!!", null, Now));
        Assert.Throws<DomainException>(() => Tenant.Create("Acme", "acme", null, null, "x", Now));
        Assert.Equal("UTC", Tenant.Create("Acme", "acme", null, null, null, Now).Timezone);
    }

    [Theory]
    [InlineData("admin")] [InlineData("api")] [InlineData("platform")] [InlineData("www")]
    public void Reserved_slugs_are_refused_for_customer_tenants(string slug) =>
        Assert.Throws<DomainException>(() => Tenant.Create("X", slug, null, null, null, Now));

    [Fact]
    public void Suspension_and_activation_follow_the_state_machine()
    {
        var t = Tenant.Create("Acme", "acme", null, null, null, Now);
        Assert.Throws<DomainException>(() => t.Activate(Now)); // already active
        t.Suspend("  unpaid  ", Now);
        Assert.Equal(TenantStatus.Suspended, t.Status);
        Assert.Equal("unpaid", t.SuspensionReason);
        Assert.Equal(Now, t.SuspendedAtUtc);
        Assert.Throws<DomainException>(() => t.Suspend(null, Now));
        t.Activate(Now.AddDays(1));
        Assert.True(t.IsActive);
        Assert.Null(t.SuspensionReason);
        Assert.Null(t.SuspendedAtUtc);
    }

    [Fact]
    public void The_platform_tenant_can_never_be_suspended_and_may_use_the_reserved_slug()
    {
        var platform = Tenant.Create("Platform", Tenant.PlatformSlug, null, null, null, Now, isPlatform: true);
        Assert.True(platform.IsPlatform);
        Assert.Throws<DomainException>(() => platform.Suspend("no", Now));
    }

    [Fact]
    public void Roles_validate_names_and_system_roles_are_immutable()
    {
        var custom = Role.Create(Guid.NewGuid(), " Closers ", "d", isSystem: false, Now);
        Assert.Equal("Closers", custom.Name);
        custom.Rename("Senior Closers", null, Now);
        custom.EnsureDeletable();
        Assert.Throws<DomainException>(() => custom.Rename("", null, Now));
        Assert.Throws<DomainException>(() => custom.Rename(new string('x', 101), null, Now));

        var system = Role.Create(Guid.NewGuid(), "Read-only", null, isSystem: true, Now);
        Assert.Throws<DomainException>(() => system.Rename("Other", null, Now));
        Assert.Throws<DomainException>(() => system.EnsureDeletable());
    }

    [Fact]
    public void Invitations_expire_are_single_use_and_cannot_be_revived()
    {
        var expires = Now.AddDays(7);
        var inv = TenantInvitation.Create(Guid.NewGuid(), "  Person@Example.COM ", Guid.NewGuid(), "hash", expires, null, Now);
        Assert.Equal("person@example.com", inv.Email);
        Assert.True(inv.CanBeAccepted(Now));
        Assert.True(inv.CanBeAccepted(expires.AddTicks(-1)));
        Assert.False(inv.CanBeAccepted(expires));
        Assert.Equal("Expired", inv.EffectiveStatus(expires));
        Assert.Throws<DomainException>(() => inv.MarkAccepted(Guid.NewGuid(), expires.AddMinutes(1)));

        var user = Guid.NewGuid();
        inv.MarkAccepted(user, Now.AddHours(1));
        Assert.Equal(InvitationStatus.Accepted, inv.Status);
        Assert.Equal(user, inv.AcceptedByUserId);
        Assert.Throws<DomainException>(() => inv.MarkAccepted(user, Now.AddHours(2)));
        Assert.Throws<DomainException>(() => inv.Revoke(Now));
        Assert.Throws<DomainException>(() => inv.ReplaceToken("x", expires, Now));
    }

    [Fact]
    public void Revoked_invitations_are_dead_and_bad_input_is_rejected()
    {
        var inv = TenantInvitation.Create(Guid.NewGuid(), "a@b.co", Guid.NewGuid(), "h", Now.AddDays(1), null, Now);
        inv.Revoke(Now);
        Assert.False(inv.CanBeAccepted(Now));
        Assert.Equal("Revoked", inv.EffectiveStatus(Now));
        Assert.Throws<DomainException>(() => TenantInvitation.Create(Guid.NewGuid(), "not-an-email", Guid.NewGuid(), "h", Now.AddDays(1), null, Now));
        Assert.Throws<DomainException>(() => TenantInvitation.Create(Guid.NewGuid(), "a@b.co", Guid.NewGuid(), "h", Now.AddDays(-1), null, Now));
    }

    [Fact]
    public void Setting_keys_are_restricted()
    {
        Assert.True(TenantSetting.IsValidKey("whatsapp.gateway-url"));
        Assert.False(TenantSetting.IsValidKey("bad key"));
        Assert.False(TenantSetting.IsValidKey("../etc"));
        Assert.False(TenantSetting.IsValidKey(new string('k', 101)));
        Assert.False(TenantSetting.IsValidKey(null));
    }

    [Fact]
    public void Refresh_tokens_expire_and_can_be_revoked_once()
    {
        var t = RefreshToken.Create(Guid.NewGuid(), Guid.NewGuid(), "h", Now.AddDays(1), null, Now);
        Assert.True(t.IsActive(Now));
        Assert.False(t.IsActive(Now.AddDays(1)));
        t.Revoke(Now, "next");
        Assert.False(t.IsActive(Now));
        var firstRevocation = t.RevokedAtUtc;
        t.Revoke(Now.AddHours(1));
        Assert.Equal(firstRevocation, t.RevokedAtUtc);
        Assert.Equal("next", t.ReplacedByTokenHash);
    }
}

public class TenantContextTests
{
    [Fact]
    public void An_empty_context_has_no_tenant_and_requiring_one_fails_closed()
    {
        var ctx = new TenantContext();
        Assert.Null(ctx.TenantId);
        Assert.False(ctx.IsPlatformScope);
        Assert.Throws<TenantContextMissingException>(() => ctx.RequireTenantId());
    }

    [Fact]
    public void Setting_clearing_and_empty_ids()
    {
        var ctx = new TenantContext();
        var id = Guid.NewGuid();
        ctx.SetTenant(id);
        Assert.Equal(id, ctx.RequireTenantId());
        Assert.Throws<TenantContextMissingException>(() => ctx.SetTenant(Guid.Empty));
        ctx.Clear();
        Assert.Throws<TenantContextMissingException>(() => ctx.RequireTenantId());
    }

    [Fact]
    public void Platform_scope_is_explicit_nestable_and_ends_when_disposed()
    {
        var ctx = new TenantContext();
        using (ctx.BeginPlatformScope())
        {
            using (ctx.BeginPlatformScope()) Assert.True(ctx.IsPlatformScope);
            Assert.True(ctx.IsPlatformScope);
            Assert.Null(ctx.TenantId); // platform scope is not a tenant
        }
        Assert.False(ctx.IsPlatformScope);
        var scope = ctx.BeginPlatformScope();
        scope.Dispose(); scope.Dispose(); // idempotent
        Assert.False(ctx.IsPlatformScope);
    }

    [Fact]
    public async Task Scope_runner_refuses_an_empty_tenant_id_before_touching_anything()
    {
        var runner = new TenantScopeRunner(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        var ran = false;
        await Assert.ThrowsAsync<TenantContextMissingException>(() => runner.RunAsync(Guid.Empty, (_, _) => { ran = true; return Task.CompletedTask; }));
        Assert.False(ran);
    }
}

public class TenantKeyHelperTests
{
    private static readonly Guid T = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Cache_keys_are_namespaced_by_tenant()
    {
        Assert.Equal("t:11111111222233334444555555555555:leads:42", TenantCacheKeys.For(T, "leads", "42"));
        Assert.NotEqual(TenantCacheKeys.For(T, "leads", "42"), TenantCacheKeys.For(Guid.NewGuid(), "leads", "42"));
    }

    [Fact]
    public void Cache_keys_fail_closed_without_a_tenant_and_reject_bad_parts()
    {
        var ctx = new TenantContext();
        Assert.Throws<TenantContextMissingException>(() => TenantCacheKeys.For(ctx, "leads", "1"));
        Assert.Throws<TenantContextMissingException>(() => TenantCacheKeys.For(Guid.Empty, "leads", "1"));
        Assert.Throws<ArgumentException>(() => TenantCacheKeys.For(T, "le:ads", "1"));
        Assert.Throws<ArgumentException>(() => TenantCacheKeys.For(T, "", "1"));
        Assert.Throws<ArgumentException>(() => TenantCacheKeys.For(T, "leads", " "));
        ctx.SetTenant(T);
        Assert.StartsWith("t:1111", TenantCacheKeys.For(ctx, "leads", "1"));
    }

    [Fact]
    public void File_paths_live_under_the_tenant_folder()
    {
        Assert.Equal("tenants/11111111222233334444555555555555/docs/a.pdf", TenantPaths.For(T, "docs\\a.pdf"));
        Assert.Equal("tenants/11111111222233334444555555555555/a/b", TenantPaths.For(T, "a//b"));
    }

    [Theory]
    [InlineData("../other/secret")] [InlineData("a/../../b")] [InlineData("/etc/passwd")] [InlineData("C:\\win")] [InlineData("..")] [InlineData("")] [InlineData("a\0b")]
    public void File_paths_reject_traversal_and_absolute_paths(string path) =>
        Assert.Throws<ArgumentException>(() => TenantPaths.For(T, path));

    [Fact]
    public void File_paths_fail_closed_without_a_tenant()
    {
        Assert.Throws<TenantContextMissingException>(() => TenantPaths.For(new TenantContext(), "a.txt"));
        Assert.Throws<TenantContextMissingException>(() => TenantPaths.For(Guid.Empty, "a.txt"));
    }
}

public class TokenAndJwtSettingsTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IConfiguration Config(string? key) =>
        new ConfigurationBuilder().AddInMemoryCollection(key is null ? [] : new Dictionary<string, string?> { ["Jwt:SigningKey"] = key }).Build();

    [Fact]
    public void Signing_key_is_required_outside_development()
    {
        Assert.Throws<InvalidOperationException>(() => JwtSettings.Resolve(Config(null), new Env("Production")));
        Assert.Throws<InvalidOperationException>(() => JwtSettings.Resolve(Config("too-short"), new Env("Production")));
        Assert.Throws<InvalidOperationException>(() => JwtSettings.Resolve(Config(null), new Env("Staging")));
        var ok = JwtSettings.Resolve(Config(new string('k', 40)), new Env("Production"));
        Assert.Equal(new string('k', 40), ok.SigningKey);
    }

    [Fact]
    public void Development_falls_back_to_a_labelled_dev_key_but_never_overrides_a_configured_one()
    {
        Assert.Equal(JwtSettings.DevelopmentOnlyKey, JwtSettings.Resolve(Config(null), new Env("Development")).SigningKey);
        Assert.Equal(new string('z', 33), JwtSettings.Resolve(Config(new string('z', 33)), new Env("Development")).SigningKey);
        Assert.True(JwtSettings.DevelopmentOnlyKey.Length >= 32);
    }

    [Fact]
    public void Token_hashes_are_deterministic_and_tokens_are_unique_and_url_safe()
    {
        Assert.Equal(TokenHasher.Hash("abc"), TokenHasher.Hash("abc"));
        Assert.NotEqual(TokenHasher.Hash("abc"), TokenHasher.Hash("abd"));
        Assert.Equal(64, TokenHasher.Hash("abc").Length);
        var a = TokenHasher.NewToken(); var b = TokenHasher.NewToken();
        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
        Assert.NotEqual(a, TokenHasher.Hash(a));
    }
}
