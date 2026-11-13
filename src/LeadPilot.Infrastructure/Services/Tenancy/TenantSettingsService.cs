using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Shared.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Services.Tenancy;

public sealed class TenantSettingsService(
    AppDbContext db, ITenantContext tenant, IAuditService audit, IClock clock, ISecretProtector secrets,
    IValidator<SetSettingRequest> validator, ILogger<TenantSettingsService> logger) : ITenantSettingsService
{
    private const int MaxSettings = 200;
    private const string Mask = "••••••••";

    public async Task<Result<TenantDto>> GetProfileAsync(CancellationToken ct = default)
    {
        var id = tenant.RequireTenantId();
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? Result.Failure<TenantDto>(Error.NotFound("Tenant")) : Result.Success(TenantQueries.ToDto(t, 0));
    }

    public Task<Result<TenantDto>> UpdateProfileAsync(UpdateTenantProfileRequest request, CancellationToken ct = default) =>
        ResultFlow.GuardAsync(async () =>
        {
            var id = tenant.RequireTenantId(); // always the active tenant; the request's TenantId is never used
            var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Result.Failure<TenantDto>(Error.NotFound("Tenant"));
            t.UpdateProfile(request.Name, request.Industry, request.Timezone, request.Locale, clock.UtcNow);
            audit.Record(new AuditEntry("Tenant.ProfileUpdated", "Tenant", id.ToString(), $"Workspace profile updated ('{t.Name}')."));
            await db.SaveChangesAsync(ct);
            return Result.Success(TenantQueries.ToDto(t, 0));
        }, logger);

    public async Task<IReadOnlyList<TenantSettingDto>> ListAsync(CancellationToken ct = default)
    {
        var id = tenant.RequireTenantId();
        var rows = await db.TenantSettings.AsNoTracking().Where(s => s.TenantId == id).OrderBy(s => s.Key).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public Task<Result<TenantSettingDto>> SetAsync(string key, SetSettingRequest request, CancellationToken ct = default) =>
        ResultFlow.GuardAsync(async () =>
        {
            var id = tenant.RequireTenantId();
            if (!TenantSetting.IsValidKey(key)) return Result.Failure<TenantSettingDto>(Error.Validation("Setting keys may contain letters, digits and . _ : - (max 100 characters)."));
            if (await validator.CheckAsync(request, ct) is { } invalid) return Result.Failure<TenantSettingDto>(invalid.Error);
            if (request.IsSecret && string.IsNullOrEmpty(request.Value)) return Result.Failure<TenantSettingDto>(Error.Validation("A secret value cannot be empty."));

            var stored = request.IsSecret ? secrets.Protect(request.Value) : request.Value;
            var existing = await db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == id && s.Key == key, ct);
            var now = clock.UtcNow;
            if (existing is null)
            {
                if (await db.TenantSettings.CountAsync(s => s.TenantId == id, ct) >= MaxSettings)
                    return Result.Failure<TenantSettingDto>(Error.Conflict($"A workspace can hold at most {MaxSettings} settings."));
                existing = TenantSetting.Create(id, key, stored, request.IsSecret, now);
                db.TenantSettings.Add(existing);
            }
            else existing.Set(stored, request.IsSecret, now);

            // Secret values never enter the audit trail.
            audit.Record(new AuditEntry("Setting.Changed", "TenantSetting", key, request.IsSecret ? $"Secret setting '{key}' changed." : $"Setting '{key}' changed."));
            await db.SaveChangesAsync(ct);
            return Result.Success(ToDto(existing));
        }, logger);

    public async Task<Result> DeleteAsync(string key, CancellationToken ct = default)
    {
        var id = tenant.RequireTenantId();
        var s = await db.TenantSettings.FirstOrDefaultAsync(x => x.TenantId == id && x.Key == key, ct);
        if (s is null) return Result.Failure(Error.NotFound("Setting"));
        db.TenantSettings.Remove(s);
        audit.Record(new AuditEntry("Setting.Deleted", "TenantSetting", key, $"Setting '{key}' deleted."));
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<string?> GetValueAsync(string key, CancellationToken ct = default)
    {
        var id = tenant.RequireTenantId();
        var s = await db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == id && x.Key == key, ct);
        return s is null ? null : s.IsSecret ? secrets.Unprotect(s.Value) : s.Value;
    }

    private static TenantSettingDto ToDto(TenantSetting s) => new(s.Key, s.IsSecret ? Mask : s.Value, s.IsSecret, s.UpdatedAtUtc ?? s.CreatedAtUtc);
}
