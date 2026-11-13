using LeadPilot.Application.Abstractions;
using LeadPilot.Domain.Common;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Database-level isolation: global query filters, the SaveChanges guard, cross-tenant FK rejection, job scopes failing closed, and seeded data.</summary>
[Collection("app")]
public class TenantDataIsolationTests(AppFixture app)
{
    private TestWorld W => app.World;

    private sealed class Ctx(IServiceScope scope) : IDisposable
    {
        public AppDbContext Db { get; } = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        public ITenantContextAccessor Accessor { get; } = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        public ITenantContext Tenant { get; } = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        public void Dispose() => scope.Dispose();
    }

    private Ctx Open(Guid? tenantId = null)
    {
        var c = new Ctx(W.Services.CreateScope());
        if (tenantId is { } t) c.Accessor.SetTenant(t);
        return c;
    }

    private async Task<Guid> SeedSettingAsync(Guid tenantId, string key)
    {
        using var c = Open(tenantId);
        var s = TenantSetting.Create(tenantId, key, "v-" + tenantId, false, DateTime.UtcNow);
        c.Db.TenantSettings.Add(s);
        await c.Db.SaveChangesAsync();
        return s.Id;
    }

    [RequiresMySqlFact]
    public async Task Queries_without_a_tenant_return_nothing_and_with_a_tenant_return_only_that_tenant()
    {
        var key = "iso." + Guid.NewGuid().ToString("N")[..6];
        await SeedSettingAsync(W.A.Id, key);
        await SeedSettingAsync(W.B.Id, key);

        using (var none = Open())
        {
            Assert.Equal(0, await none.Db.TenantSettings.CountAsync());
            Assert.Equal(0, await none.Db.TenantRoles.CountAsync());
            Assert.Equal(0, await none.Db.TenantInvitations.CountAsync());
            Assert.Equal(0, await none.Db.AuditLogs.CountAsync());
            Assert.Equal(0, await none.Db.TenantUserRoles.CountAsync());
            Assert.Equal(0, await none.Db.UserTenants.CountAsync());
        }
        using (var a = Open(W.A.Id))
        {
            var rows = await a.Db.TenantSettings.Where(s => s.Key == key).ToListAsync();
            Assert.Equal(W.A.Id, Assert.Single(rows).TenantId);
            Assert.All(await a.Db.TenantRoles.ToListAsync(), r => Assert.Equal(W.A.Id, r.TenantId));
            Assert.All(await a.Db.UserTenants.ToListAsync(), m => Assert.Equal(W.A.Id, m.TenantId));
        }
        using (var b = Open(W.B.Id))
            Assert.Equal(W.B.Id, Assert.Single(await b.Db.TenantSettings.Where(s => s.Key == key).ToListAsync()).TenantId);
    }

    [RequiresMySqlFact]
    public async Task Writes_without_a_tenant_context_fail_closed()
    {
        using var c = Open();
        c.Db.TenantSettings.Add(TenantSetting.Create(W.A.Id, "nocontext." + Guid.NewGuid().ToString("N")[..6], "v", false, DateTime.UtcNow));
        await Assert.ThrowsAsync<TenantContextMissingException>(() => c.Db.SaveChangesAsync());

        using var stamped = Open();
        stamped.Db.TenantSettings.Add(TenantSetting.Create(Guid.Empty, "nocontext2", "v", false, DateTime.UtcNow)); // nothing to stamp from either
        await Assert.ThrowsAsync<TenantContextMissingException>(() => stamped.Db.SaveChangesAsync());
    }

    [RequiresMySqlFact]
    public async Task Writes_for_another_tenant_are_rejected_and_inserts_are_stamped_from_context()
    {
        using (var a = Open(W.A.Id))
        {
            a.Db.TenantSettings.Add(TenantSetting.Create(W.B.Id, "cross." + Guid.NewGuid().ToString("N")[..6], "v", false, DateTime.UtcNow));
            await Assert.ThrowsAsync<CrossTenantViolationException>(() => a.Db.SaveChangesAsync());
        }
        using (var a = Open(W.A.Id))
        {
            var key = "stamped." + Guid.NewGuid().ToString("N")[..6];
            var s = TenantSetting.Create(Guid.Empty, key, "v", false, DateTime.UtcNow);
            a.Db.TenantSettings.Add(s);
            await a.Db.SaveChangesAsync();
            Assert.Equal(W.A.Id, s.TenantId); // stamped by the interceptor
            a.Db.TenantSettings.Remove(s);
            await a.Db.SaveChangesAsync();
        }
    }

    [RequiresMySqlFact]
    public async Task Another_tenants_rows_cannot_be_modified_deleted_or_re_homed()
    {
        var key = "owned." + Guid.NewGuid().ToString("N")[..6];
        var bId = await SeedSettingAsync(W.B.Id, key);

        using (var a = Open(W.A.Id)) // even bypassing the filter, the guard stops the write
        {
            var foreign = await a.Db.TenantSettings.IgnoreQueryFilters().SingleAsync(s => s.Id == bId);
            foreign.Set("tampered", false, DateTime.UtcNow);
            await Assert.ThrowsAsync<CrossTenantViolationException>(() => a.Db.SaveChangesAsync());
        }
        using (var a = Open(W.A.Id))
        {
            var foreign = await a.Db.TenantSettings.IgnoreQueryFilters().SingleAsync(s => s.Id == bId);
            a.Db.TenantSettings.Remove(foreign);
            await Assert.ThrowsAsync<CrossTenantViolationException>(() => a.Db.SaveChangesAsync());
        }
        using (var b = Open(W.B.Id)) // TenantId is immutable even for the owner
        {
            var own = await b.Db.TenantSettings.SingleAsync(s => s.Id == bId);
            b.Db.Entry(own).Property(nameof(ITenantOwned.TenantId)).CurrentValue = W.A.Id;
            await Assert.ThrowsAsync<CrossTenantViolationException>(() => b.Db.SaveChangesAsync());
        }
        using (var b = Open(W.B.Id))
            Assert.Equal("v-" + W.B.Id, (await b.Db.TenantSettings.SingleAsync(s => s.Id == bId)).Value);
    }

    [RequiresMySqlFact]
    public async Task Cross_tenant_foreign_keys_are_rejected_even_inside_a_platform_scope()
    {
        using var c = Open();
        using var _ = c.Accessor.BeginPlatformScope(); // platform scope relaxes "which tenant", never "which tenant's data may be linked"
        var someUser = W.AAdmin.Id;
        var anyPermission = await c.Db.Permissions.Select(p => p.Id).FirstAsync();

        // A's membership row pointing at B's role
        c.Db.TenantUserRoles.Add(UserRole.Create(W.A.Id, someUser, W.RoleB, null, DateTime.UtcNow));
        var ex = await Assert.ThrowsAsync<CrossTenantViolationException>(() => c.Db.SaveChangesAsync());
        Assert.Contains("another tenant", ex.Message);
        c.Db.ChangeTracker.Clear();

        c.Db.RolePermissions.Add(RolePermission.Create(W.A.Id, W.RoleB, anyPermission));
        await Assert.ThrowsAsync<CrossTenantViolationException>(() => c.Db.SaveChangesAsync());
        c.Db.ChangeTracker.Clear();

        c.Db.TenantInvitations.Add(TenantInvitation.Create(W.A.Id, "fk@test.test", W.RoleB, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddDays(1), null, DateTime.UtcNow));
        await Assert.ThrowsAsync<CrossTenantViolationException>(() => c.Db.SaveChangesAsync());
        c.Db.ChangeTracker.Clear();

        // principal created in the same unit of work (tracked, not yet in the database)
        var newRoleInB = Role.Create(W.B.Id, "Tracked " + Guid.NewGuid().ToString("N")[..6], null, false, DateTime.UtcNow);
        c.Db.TenantRoles.Add(newRoleInB);
        c.Db.TenantUserRoles.Add(UserRole.Create(W.A.Id, someUser, newRoleInB.Id, null, DateTime.UtcNow));
        await Assert.ThrowsAsync<CrossTenantViolationException>(() => c.Db.SaveChangesAsync());
        c.Db.ChangeTracker.Clear();

        // same-tenant links are fine
        Assert.True(await c.Db.TenantRoles.IgnoreQueryFilters().AnyAsync(r => r.Id == W.RoleA && r.TenantId == W.A.Id));
        c.Db.TenantRoles.Add(Role.Create(W.A.Id, "Same tenant " + Guid.NewGuid().ToString("N")[..6], null, false, DateTime.UtcNow));
        await c.Db.SaveChangesAsync();
    }

    [RequiresMySqlFact]
    public async Task Audit_entries_are_append_only_and_tenant_scoped()
    {
        using var a = Open(W.A.Id);
        var audit = a.Db.AuditLogs.Add(AuditLog.Create(W.A.Id, null, null, "t@t", "Test.Append", null, null, "one", null, null, DateTime.UtcNow));
        await a.Db.SaveChangesAsync();

        var entry = audit.Entity;
        a.Db.Remove(entry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Db.SaveChangesAsync());
        a.Db.ChangeTracker.Clear();

        var again = await a.Db.AuditLogs.FirstAsync(x => x.Id == entry.Id);
        a.Db.Entry(again).Property(x => x.Summary).CurrentValue = "rewritten";
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Db.SaveChangesAsync());

        using var b = Open(W.B.Id);
        Assert.DoesNotContain(await b.Db.AuditLogs.ToListAsync(), x => x.Id == entry.Id);
    }

    [RequiresMySqlFact]
    public async Task Tenant_scope_runner_for_jobs_and_consumers_fails_closed()
    {
        var runner = W.Services.GetRequiredService<ITenantScopeRunner>();
        var ran = false;

        await Assert.ThrowsAsync<TenantContextMissingException>(() => runner.RunAsync(Guid.Empty, (_, _) => { ran = true; return Task.CompletedTask; }));
        await Assert.ThrowsAsync<TenantUnavailableException>(() => runner.RunAsync(Guid.NewGuid(), (_, _) => { ran = true; return Task.CompletedTask; }));
        Assert.False(ran);

        // an active tenant: the work sees exactly that tenant, nothing else
        Guid? seen = null; var visibleRoleTenants = new List<Guid>();
        await runner.RunAsync(W.A.Id, async (sp, ct) =>
        {
            seen = sp.GetRequiredService<ITenantContext>().TenantId;
            visibleRoleTenants = await sp.GetRequiredService<AppDbContext>().TenantRoles.Select(r => r.TenantId).Distinct().ToListAsync(ct);
        });
        Assert.Equal(W.A.Id, seen);
        Assert.Equal([W.A.Id], visibleRoleTenants);

        // a job that forgot to establish a tenant sees nothing and cannot write
        using var unscoped = W.Services.CreateScope();
        var db = unscoped.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.TenantRoles.CountAsync());
        db.TenantSettings.Add(TenantSetting.Create(W.A.Id, "job.write", "v", false, DateTime.UtcNow));
        await Assert.ThrowsAsync<TenantContextMissingException>(() => db.SaveChangesAsync());

        // explicit platform jobs are the only unscoped path, and they must say so
        var platformSaw = false;
        await runner.RunPlatformJobAsync((sp, _) => { platformSaw = sp.GetRequiredService<ITenantContext>().IsPlatformScope; return Task.CompletedTask; });
        Assert.True(platformSaw);
    }

    [RequiresMySqlFact]
    public async Task Suspended_tenants_do_not_run_jobs()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        using var scope = W.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<LeadPilot.Application.Contracts.ITenantAdminService>();
        var t = (await admin.CreateAsync(new("Job " + suffix, "job-" + suffix, null, null, null, null))).Value.Tenant;
        await admin.SuspendAsync(t.Id, "test");
        var runner = W.Services.GetRequiredService<ITenantScopeRunner>();
        await Assert.ThrowsAsync<TenantUnavailableException>(() => runner.RunAsync(t.Id, (_, _) => Task.CompletedTask));
        await admin.ActivateAsync(t.Id);
        await runner.RunAsync(t.Id, (_, _) => Task.CompletedTask);
    }

    [RequiresMySqlFact]
    public async Task Seeding_created_the_permission_catalog_platform_tenant_and_default_roles()
    {
        await W.WithDb(async db =>
        {
            var codes = await db.Permissions.Select(p => p.Code).ToListAsync();
            Assert.Equal(PermissionCatalog.All.Select(p => p.Code).Order(), codes.Order());

            var platform = await db.Tenants.SingleAsync(t => t.IsPlatform);
            Assert.Equal(Tenant.PlatformSlug, platform.Slug);
            var platformRoles = await db.TenantRoles.IgnoreQueryFilters().Where(r => r.TenantId == platform.Id).Select(r => r.Name).ToListAsync();
            Assert.Equal(new[] { SystemRoles.PlatformOwner, SystemRoles.SaasAdministrator }.Order(), platformRoles.Order());

            foreach (var tenantId in new[] { W.A.Id, W.B.Id })
            {
                var names = await db.TenantRoles.IgnoreQueryFilters().Where(r => r.TenantId == tenantId && r.IsSystem).Select(r => r.Name).ToListAsync();
                Assert.Equal(SystemRoles.For(platformTenant: false).Select(r => r.Name).Order(), names.Order());
            }

            // role → permission mapping matches the definitions, and customer-tenant roles never carry platform permissions
            var grants = await (from rp in db.RolePermissions.IgnoreQueryFilters()
                                join r in db.TenantRoles.IgnoreQueryFilters() on rp.RoleId equals r.Id
                                join p in db.Permissions on rp.PermissionId equals p.Id
                                where r.IsSystem
                                select new { r.TenantId, r.Name, p.Code, p.IsPlatform }).ToListAsync();
            Assert.DoesNotContain(grants, g => g.TenantId != platform.Id && g.IsPlatform);
            foreach (var def in SystemRoles.All)
            {
                var actual = grants.Where(g => g.Name == def.Name && (def.PlatformOnly ? g.TenantId == platform.Id : g.TenantId == W.A.Id)).Select(g => g.Code).Order().ToList();
                Assert.Equal(def.Permissions.Order().ToList(), actual);
            }
            Assert.Equal(PermissionCatalog.All.Count, grants.Count(g => g.Name == SystemRoles.PlatformOwner));
            return 0;
        });
    }

    [RequiresMySqlFact]
    public async Task The_seeded_first_admin_is_the_platform_owner_in_the_platform_tenant()
    {
        using var scope = W.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<LeadPilot.Infrastructure.Identity.ApplicationUser>>();
        var admin = await users.FindByEmailAsync(AppFixture.AdminEmail);
        Assert.NotNull(admin);
        var perms = await scope.ServiceProvider.GetRequiredService<LeadPilot.Application.Contracts.IPermissionEvaluator>().GetAsync(admin!.Id, W.PlatformTenantId);
        Assert.True(perms.Has(PermissionCodes.TenantsManage));
        Assert.True(perms.Has(PermissionCodes.PlatformSwitchTenant));
        // and holds nothing in customer tenants unless they switch in
        var inA = await scope.ServiceProvider.GetRequiredService<LeadPilot.Application.Contracts.IMembershipResolver>().ResolveAsync(admin.Id, W.A.Id);
        Assert.True(inA.ViaPlatformAccess);
    }
}
