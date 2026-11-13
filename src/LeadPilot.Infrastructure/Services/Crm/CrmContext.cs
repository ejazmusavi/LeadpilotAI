using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Services.Crm;

/// <summary>Everything CRM services share: the database, the active tenant, the acting user, audit/activity helpers and tenant-membership checks.</summary>
public sealed class CrmContext(AppDbContext db, ITenantContext tenant, ICurrentUser user, IClock clock, IAuditService audit, IPermissionEvaluator permissions, ITenantSettingsService settings, ILogger<CrmContext> logger)
{
    private string? _countryCode; private bool _countryLoaded;
    private bool _defaultsEnsured;

    public AppDbContext Db => db;
    public Guid TenantId => tenant.RequireTenantId();
    public DateTime Now => clock.UtcNow;
    public Guid? UserId => user.UserId;
    public string? Actor => user.Email;
    public IAuditService Audit => audit;
    public IPermissionEvaluator Permissions => permissions;
    public ILogger Logger => logger;

    public async Task<string?> DefaultCountryCodeAsync(CancellationToken ct)
    {
        if (_countryLoaded) return _countryCode;
        _countryCode = await settings.GetValueAsync(CrmOptions.DefaultCountryCodeSetting, ct);
        _countryLoaded = true;
        return _countryCode;
    }

    public void Activity(Guid leadId, LeadActivityType type, string summary, object? payload = null) =>
        db.LeadActivities.Add(LeadActivity.Create(TenantId, leadId, type, summary, payload is null ? null : JsonSerializer.Serialize(payload), UserId, Now));

    public void Record(string action, string entityType, Guid id, string summary) =>
        audit.Record(new AuditEntry(action, entityType, id.ToString(), summary));

    public async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var set = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        if (set.Count == 0) return [];
        return await db.Users.AsNoTracking().Where(u => set.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName }).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    public async Task<bool> IsActiveMemberAsync(Guid userId, CancellationToken ct)
    {
        var tenantId = TenantId;
        return await (from m in db.UserTenants.AsNoTracking()
                      join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                      where m.TenantId == tenantId && m.UserId == userId && m.Status == MembershipStatus.Active && u.IsActive
                      select m.UserId).AnyAsync(ct);
    }

    /// <summary>Creates the tenant's default pipeline, lead statuses and scoring rules when it has none yet (new tenants get them at creation; this covers anything else).</summary>
    public async Task EnsureDefaultsAsync(CancellationToken ct)
    {
        if (_defaultsEnsured) return;
        var tenantId = TenantId;
        var added = false;
        if (!await db.Pipelines.AnyAsync(p => p.TenantId == tenantId, ct)) { CrmDefaults.AddPipeline(db, tenantId, Now); added = true; }
        if (!await db.LeadStatuses.AnyAsync(s => s.TenantId == tenantId, ct)) { CrmDefaults.AddStatuses(db, tenantId, Now); added = true; }
        if (!await db.ScoringRuleSets.AnyAsync(s => s.TenantId == tenantId, ct)) { CrmDefaults.AddScoring(db, tenantId, Now); added = true; }
        if (added)
        {
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex))
            {
                // A concurrent request provisioned the defaults first: drop our attempt and use theirs.
                foreach (var e in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList()) e.State = EntityState.Detached;
            }
        }
        _defaultsEnsured = true;
    }

    public static string Mask(string value)
    {
        if (value.Contains('@')) { var at = value.IndexOf('@'); return value[0] + "***" + value[at..]; }
        return value.Length <= 4 ? "****" : value[..Math.Min(4, value.Length - 4)] + "****" + value[^3..];
    }
}

internal static class CrmFlow
{
    public static async Task<Result<T>> GuardAsync<T>(Func<Task<Result<T>>> work, ILogger logger)
    {
        try { return await ResultFlow.GuardAsync(work, logger); }
        catch (DbUpdateConcurrencyException) { return Result.Failure<T>(Error.Conflict("The record was changed by someone else. Reload it and try again.")); }
    }

    public static async Task<Result> GuardAsync(Func<Task<Result>> work, ILogger logger)
    {
        try { return await work(); }
        catch (DomainException ex) { return Result.Failure(Error.Validation(ex.Message)); }
        catch (DbUpdateConcurrencyException) { return Result.Failure(Error.Conflict("The record was changed by someone else. Reload it and try again.")); }
        catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex))
        {
            logger.LogInformation(ex, "Duplicate key rejected");
            return Result.Failure(Error.Conflict("That record already exists."));
        }
    }

    public static (int Page, int Size) Page(int page, int pageSize, int max = 100) => (Math.Max(1, page), Math.Clamp(pageSize, 1, max));
}

/// <summary>Default CRM data of a tenant. Pure inserts: callers save.</summary>
public static class CrmDefaults
{
    public static void AddPipeline(AppDbContext db, Guid tenantId, DateTime now)
    {
        var p = Pipeline.Create(tenantId, DefaultPipeline.Name, isDefault: true, now);
        db.Pipelines.Add(p);
        var pos = 0;
        foreach (var (name, kind) in DefaultPipeline.Stages) db.PipelineStages.Add(PipelineStage.Create(tenantId, p.Id, name, ++pos * 10, kind, now));
    }

    public static void AddStatuses(AppDbContext db, Guid tenantId, DateTime now)
    {
        var order = 0;
        foreach (var (name, closed, isDefault) in DefaultPipeline.Statuses) db.LeadStatuses.Add(LeadStatus.Create(tenantId, name, ++order * 10, closed, isDefault, now));
    }

    public static void AddScoring(AppDbContext db, Guid tenantId, DateTime now)
    {
        var set = ScoringRuleSet.Create(tenantId, 1, "Default rules", now, null);
        db.ScoringRuleSets.Add(set);
        foreach (var r in LeadScoringEngine.DefaultRules) db.ScoringRules.Add(ScoringRule.Create(tenantId, set.Id, r.Dimension, r.Target, r.Points, r.Description, r.Enabled));
    }

    public static void AddAll(AppDbContext db, Guid tenantId, DateTime now) { AddPipeline(db, tenantId, now); AddStatuses(db, tenantId, now); AddScoring(db, tenantId, now); }
}
