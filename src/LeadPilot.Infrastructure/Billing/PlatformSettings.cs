using System.Globalization;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Contracts;
using LeadPilot.Domain.Billing;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Billing;

/// <summary>Platform-wide settings (global table, no tenant). Reads fall back to <see cref="PlatformDefaults"/>; saves are audited as platform actions.</summary>
public sealed class PlatformSettings(AppDbContext db, IClock clock, ICurrentUser user, IAuditService audit, ITenantContext tenant, ITenantContextAccessor accessor) : IPlatformSettings
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var row = await db.PlatformSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (row is not null) return row.Length == 0 ? null : row;
        return PlatformDefaults.Values.GetValueOrDefault(key);
    }

    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) =>
        int.TryParse(await GetAsync(key, ct), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) =>
        bool.TryParse(await GetAsync(key, ct), out var v) ? v : fallback;

    public async Task<PlatformSettingsDto> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await db.PlatformSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        string? Get(string k) => rows.TryGetValue(k, out var v) ? (v.Length == 0 ? null : v) : PlatformDefaults.Values.GetValueOrDefault(k);
        int I(string k, int f) => int.TryParse(Get(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : f;
        bool B(string k, bool f) => bool.TryParse(Get(k), out var v) ? v : f;
        Guid? G(string k) => Guid.TryParse(Get(k), out var v) ? v : null;
        return new PlatformSettingsDto(B(PlatformSettingKeys.RegistrationEnabled, false), G(PlatformSettingKeys.RegistrationPlanId), G(PlatformSettingKeys.DefaultPlanId), I(PlatformSettingKeys.GraceDays, 7),
            I(PlatformSettingKeys.RenewalLeadDays, 7), I(PlatformSettingKeys.InvoiceDueDays, 7), Get(PlatformSettingKeys.TaxName),
            decimal.TryParse(Get(PlatformSettingKeys.TaxRatePercent), NumberStyles.Number, CultureInfo.InvariantCulture, out var tax) ? tax : 0m,
            Get(PlatformSettingKeys.IssuerText), Get(PlatformSettingKeys.PaymentInstructions), Get(PlatformSettingKeys.InvoicePrefix) ?? "INV-",
            I(PlatformSettingKeys.MaxRegistrationsPerIpPerHour, 3), I(PlatformSettingKeys.MaxRegistrationsPerDomainPerDay, 20), Get(PlatformSettingKeys.ExtraBlockedDomains), B(PlatformSettingKeys.CaptchaRequired, false));
    }

    public async Task<Result<PlatformSettingsDto>> SaveAsync(PlatformSettingsDto s, CancellationToken ct = default)
    {
        if (s.GraceDays is < 0 or > 90) return Result.Failure<PlatformSettingsDto>(Error.Validation("The grace period is 0 to 90 days."));
        if (s.RenewalLeadDays is < 1 or > 90) return Result.Failure<PlatformSettingsDto>(Error.Validation("Renewal invoices are issued 1 to 90 days before the period ends."));
        if (s.InvoiceDueDays is < 0 or > 90) return Result.Failure<PlatformSettingsDto>(Error.Validation("Invoices are due 0 to 90 days after issue."));
        if (s.TaxRatePercent is < 0 or > 100 || decimal.Round(s.TaxRatePercent, 2) != s.TaxRatePercent) return Result.Failure<PlatformSettingsDto>(Error.Validation("The tax rate is 0 to 100 percent with at most two decimals."));
        if (s.MaxRegistrationsPerIpPerHour is < 1 or > 1000 || s.MaxRegistrationsPerDomainPerDay is < 1 or > 10000) return Result.Failure<PlatformSettingsDto>(Error.Validation("Registration limits must be positive."));
        if (string.IsNullOrWhiteSpace(s.InvoicePrefix) || s.InvoicePrefix.Length > 12) return Result.Failure<PlatformSettingsDto>(Error.Validation("The invoice number prefix is 1 to 12 characters."));
        foreach (var id in new[] { s.RegistrationPlanId, s.DefaultPlanId })
            if (id is { } pid && !await db.Plans.AnyAsync(p => p.Id == pid && p.Status == PlanStatus.Active, ct)) return Result.Failure<PlatformSettingsDto>(Error.Validation("The selected plan does not exist or is archived."));
        if (s.RegistrationEnabled && s.RegistrationPlanId is null) return Result.Failure<PlatformSettingsDto>(Error.Validation("Choose the plan that new sign-ups start on before opening registration."));

        var now = clock.UtcNow; var actor = user.Email;
        var values = new Dictionary<string, string>
        {
            [PlatformSettingKeys.RegistrationEnabled] = s.RegistrationEnabled ? "true" : "false",
            [PlatformSettingKeys.RegistrationPlanId] = s.RegistrationPlanId?.ToString() ?? "",
            [PlatformSettingKeys.DefaultPlanId] = s.DefaultPlanId?.ToString() ?? "",
            [PlatformSettingKeys.GraceDays] = s.GraceDays.ToString(CultureInfo.InvariantCulture),
            [PlatformSettingKeys.RenewalLeadDays] = s.RenewalLeadDays.ToString(CultureInfo.InvariantCulture),
            [PlatformSettingKeys.InvoiceDueDays] = s.InvoiceDueDays.ToString(CultureInfo.InvariantCulture),
            [PlatformSettingKeys.TaxName] = s.TaxName?.Trim() ?? "",
            [PlatformSettingKeys.TaxRatePercent] = s.TaxRatePercent.ToString(CultureInfo.InvariantCulture),
            [PlatformSettingKeys.IssuerText] = s.IssuerText?.Trim() ?? "",
            [PlatformSettingKeys.PaymentInstructions] = s.PaymentInstructions?.Trim() ?? "",
            [PlatformSettingKeys.InvoicePrefix] = s.InvoicePrefix.Trim(),
            [PlatformSettingKeys.MaxRegistrationsPerIpPerHour] = s.MaxRegistrationsPerIpPerHour.ToString(CultureInfo.InvariantCulture),
            [PlatformSettingKeys.MaxRegistrationsPerDomainPerDay] = s.MaxRegistrationsPerDomainPerDay.ToString(CultureInfo.InvariantCulture),
            [PlatformSettingKeys.ExtraBlockedDomains] = s.ExtraBlockedDomains?.Trim() ?? "",
            [PlatformSettingKeys.CaptchaRequired] = s.CaptchaRequired ? "true" : "false",
        };
        var existing = await db.PlatformSettings.ToDictionaryAsync(x => x.Key, ct);
        var changed = new List<string>();
        foreach (var (k, v) in values)
        {
            if (existing.TryGetValue(k, out var row)) { if (row.Value != v) { row.Set(v, now, actor); changed.Add(k); } }
            else if (v != (PlatformDefaults.Values.GetValueOrDefault(k) ?? "")) { db.PlatformSettings.Add(PlatformSetting.Create(k, v, now, actor)); changed.Add(k); }
        }
        if (changed.Count > 0)
        {
            using var _ = accessor.BeginPlatformScope();
            audit.Record(new AuditEntry("Platform.Settings.Updated", "PlatformSetting", null, "Platform settings changed: " + string.Join(", ", changed) + ".", tenant.TenantId));
            await db.SaveChangesAsync(ct);
        }
        return Result.Success(await GetAllAsync(ct));
    }
}
