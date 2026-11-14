using System.Reflection;
using LeadPilot.Application.Abstractions;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Domain.Workflows;
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
        var lookups = new LookupCache(db);

        foreach (var entry in entries)
        {
            if (entry.Entity is IAppendOnly && entry.State != EntityState.Added)
                throw new InvalidOperationException($"{entry.Metadata.ShortName()} rows are append-only: they cannot be changed or deleted.");

            // a sealed row (a step or trigger of an activated workflow version) can never be changed or removed
            if (entry.Entity is ISealable && entry.State is EntityState.Modified or EntityState.Deleted && entry.Property(nameof(ISealable.IsSealed)).OriginalValue is true)
                throw new InvalidOperationException($"{entry.Metadata.ShortName()} rows of an activated workflow version are immutable: create a new version instead.");

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
                    await EnsureForeignKeysSameTenantAsync(db, lookups, entry, tid, async, ct);
                    break;

                case EntityState.Modified:
                    if (prop.IsModified) throw new CrossTenantViolationException($"TenantId of {entry.Metadata.ShortName()} is immutable.");
                    EnsureActiveTenantOrPlatform(tenant, (Guid)prop.OriginalValue!, entry);
                    if (entry.Properties.Any(p => p.IsModified && p.Metadata.IsForeignKey()))
                        await EnsureForeignKeysSameTenantAsync(db, lookups, entry, (Guid)prop.CurrentValue!, async, ct);
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

    private static async Task EnsureForeignKeysSameTenantAsync(AppDbContext db, LookupCache cache, EntityEntry entry, Guid entityTenantId, bool async, CancellationToken ct)
    {
        foreach (var fk in entry.Metadata.GetForeignKeys())
        {
            var principalType = fk.PrincipalEntityType.ClrType;
            if (!typeof(ITenantOwned).IsAssignableFrom(principalType) || fk.Properties.Count != 1) continue;

            var value = entry.Property(fk.Properties[0].Name).CurrentValue as Guid?;
            if (value is null || value == Guid.Empty) continue;

            // Principal tracked in the same unit of work? Compare directly; otherwise ask the database (once per principal per save).
            Guid? principalTenant;
            if (cache.TryGetTracked(fk.PrincipalEntityType, fk.PrincipalKey.Properties[0].Name, value.Value, out var trackedTenant)) principalTenant = trackedTenant;
            else if (cache.TryGetLooked(principalType, value.Value, out var cached)) principalTenant = cached;
            else
            {
                var method = (async ? LookupAsyncMethod : LookupSyncMethod).MakeGenericMethod(principalType);
                principalTenant = async
                    ? await (Task<Guid?>)method.Invoke(null, [db, value.Value, ct])!
                    : (Guid?)method.Invoke(null, [db, value.Value, ct]);
                cache.RememberLooked(principalType, value.Value, principalTenant);
            }

            if (principalTenant is not null && principalTenant != entityTenantId)
                throw new CrossTenantViolationException($"{entry.Metadata.ShortName()} references a {fk.PrincipalEntityType.ShortName()} that belongs to another tenant.");
        }
    }

    /// <summary>Per-save memo: tenant of tracked principals (indexed once) and of principals already fetched from the database.</summary>
    private sealed class LookupCache(AppDbContext db)
    {
        private Dictionary<(IEntityType, string, Guid), Guid>? _tracked;
        private readonly Dictionary<(Type, Guid), Guid?> _looked = [];

        public bool TryGetTracked(IEntityType type, string keyName, Guid id, out Guid tenant)
        {
            _tracked ??= BuildTracked();
            return _tracked.TryGetValue((type, keyName, id), out tenant);
        }

        private Dictionary<(IEntityType, string, Guid), Guid> BuildTracked()
        {
            var map = new Dictionary<(IEntityType, string, Guid), Guid>();
            foreach (var e in db.ChangeTracker.Entries())
            {
                if (e.Entity is not ITenantOwned || e.State == EntityState.Detached) continue;
                var key = e.Metadata.FindPrimaryKey();
                if (key is null || key.Properties.Count != 1) continue;
                if (e.Property(key.Properties[0].Name).CurrentValue is Guid id && e.Property(nameof(ITenantOwned.TenantId)).CurrentValue is Guid t)
                    map[(e.Metadata, key.Properties[0].Name, id)] = t;
            }
            return map;
        }

        public bool TryGetLooked(Type type, Guid id, out Guid? tenant) => _looked.TryGetValue((type, id), out tenant);
        public void RememberLooked(Type type, Guid id, Guid? tenant) => _looked[(type, id)] = tenant;
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
