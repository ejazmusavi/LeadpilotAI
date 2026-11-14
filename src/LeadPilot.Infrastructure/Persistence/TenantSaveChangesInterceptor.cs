using System.Reflection;
using LeadPilot.Application.Abstractions;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>
/// Last line of defence for tenant isolation, run on every SaveChanges:
/// stamps TenantId on inserts, rejects writes outside the active tenant (unless in an explicit platform scope),
/// makes TenantId immutable, rejects foreign keys that point at another tenant's rows, and keeps AuditLog append-only.
/// </summary>
public sealed class TenantSaveChangesInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is AppDbContext db) ValidateAsync(db, async: false, default).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db) await ValidateAsync(db, async: true, cancellationToken);
        return result;
    }

    private static async Task ValidateAsync(AppDbContext db, bool async, CancellationToken ct)
    {
        var tenant = db.Tenant;
        var entries = db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();

        foreach (var entry in entries)
        {
            if (entry.Entity is AuditLog && entry.State != EntityState.Added)
                throw new InvalidOperationException("Audit log entries are append-only.");

            if (entry.Entity is not ITenantOwned) continue;

            var prop = entry.Property(nameof(ITenantOwned.TenantId));
            switch (entry.State)
            {
                case EntityState.Added:
                    var tid = (Guid)prop.CurrentValue!;
                    if (tid == Guid.Empty)
                    {
                        if (tenant.TenantId is null) throw new TenantContextMissingException();
                        prop.CurrentValue = tid = tenant.TenantId.Value;
                    }
                    EnsureActiveTenantOrPlatform(tenant, tid, entry);
                    await EnsureForeignKeysSameTenantAsync(db, entry, tid, async, ct);
                    break;

                case EntityState.Modified:
                    if (prop.IsModified) throw new CrossTenantViolationException($"TenantId of {entry.Metadata.ShortName()} is immutable.");
                    EnsureActiveTenantOrPlatform(tenant, (Guid)prop.OriginalValue!, entry);
                    if (entry.Properties.Any(p => p.IsModified && p.Metadata.IsForeignKey()))
                        await EnsureForeignKeysSameTenantAsync(db, entry, (Guid)prop.CurrentValue!, async, ct);
                    break;

                case EntityState.Deleted:
                    EnsureActiveTenantOrPlatform(tenant, (Guid)prop.OriginalValue!, entry);
                    break;
            }
        }
    }

    private static void EnsureActiveTenantOrPlatform(ITenantContext tenant, Guid entityTenantId, EntityEntry entry)
    {
        if (tenant.IsPlatformScope) return;
        if (tenant.TenantId is null) throw new TenantContextMissingException($"Cannot write {entry.Metadata.ShortName()} without a tenant context.");
        if (tenant.TenantId.Value != entityTenantId)
            throw new CrossTenantViolationException($"Attempted to write {entry.Metadata.ShortName()} belonging to another tenant.");
    }

    private static async Task EnsureForeignKeysSameTenantAsync(AppDbContext db, EntityEntry entry, Guid entityTenantId, bool async, CancellationToken ct)
    {
        foreach (var fk in entry.Metadata.GetForeignKeys())
        {
            var principalType = fk.PrincipalEntityType.ClrType;
            if (!typeof(ITenantOwned).IsAssignableFrom(principalType) || fk.Properties.Count != 1) continue;

            var value = entry.Property(fk.Properties[0].Name).CurrentValue as Guid?;
            if (value is null || value == Guid.Empty) continue;

            // Principal tracked in the same unit of work? Compare directly; otherwise ask the database.
            var tracked = db.ChangeTracker.Entries().FirstOrDefault(e => e.Metadata == fk.PrincipalEntityType
                && Equals(e.Property(fk.PrincipalKey.Properties[0].Name).CurrentValue, value));
            Guid? principalTenant;
            if (tracked is not null) principalTenant = (Guid)tracked.Property(nameof(ITenantOwned.TenantId)).CurrentValue!;
            else
            {
                var method = (async ? LookupAsyncMethod : LookupSyncMethod).MakeGenericMethod(principalType);
                principalTenant = async
                    ? await (Task<Guid?>)method.Invoke(null, [db, value.Value, ct])!
                    : (Guid?)method.Invoke(null, [db, value.Value, ct]);
            }

            if (principalTenant is not null && principalTenant != entityTenantId)
                throw new CrossTenantViolationException($"{entry.Metadata.ShortName()} references a {fk.PrincipalEntityType.ShortName()} that belongs to another tenant.");
        }
    }

    private static readonly MethodInfo LookupAsyncMethod = typeof(TenantSaveChangesInterceptor).GetMethod(nameof(LookupTenantAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo LookupSyncMethod = typeof(TenantSaveChangesInterceptor).GetMethod(nameof(LookupTenant), BindingFlags.NonPublic | BindingFlags.Static)!;

    // Must see rows of every tenant to detect a foreign reference, hence IgnoreQueryFilters (allow-listed by an architecture test).
    private static Task<Guid?> LookupTenantAsync<T>(AppDbContext db, Guid id, CancellationToken ct) where T : class, ITenantOwned =>
        db.Set<T>().IgnoreQueryFilters().AsNoTracking().Where(e => EF.Property<Guid>(e, "Id") == id)
            .Select(e => (Guid?)EF.Property<Guid>(e, nameof(ITenantOwned.TenantId))).FirstOrDefaultAsync(ct);

    private static Guid? LookupTenant<T>(AppDbContext db, Guid id, CancellationToken ct) where T : class, ITenantOwned =>
        db.Set<T>().IgnoreQueryFilters().AsNoTracking().Where(e => EF.Property<Guid>(e, "Id") == id)
            .Select(e => (Guid?)EF.Property<Guid>(e, nameof(ITenantOwned.TenantId))).FirstOrDefault();
}
