using LeadPilot.Application.Agents;
using LeadPilot.Application.Conversion;
using LeadPilot.Domain.Conversion;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Conversion;

/// <summary>
/// The Phase 8 port the AI's CheckDemoAvailability / BookDemo tools and the workflow "schedule demo" step talk to, now backed by the real demo service. A booking made through it follows exactly the same rules as
/// a person's (only an available slot; no double booking; no past; one upcoming demo per lead) and is recorded as an AUTOMATION booking.
/// </summary>
internal sealed class DemoSchedulerPort(IDemoService demos) : IDemoScheduler
{
    public async Task<PortResult<IReadOnlyList<DemoSlot>>> AvailabilityAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var slots = await demos.SlotsAsync(fromUtc, toUtc, null, ct);
        return PortResult<IReadOnlyList<DemoSlot>>.Ok(slots.Select(s => new DemoSlot(s.SlotId, s.StartUtc, s.EndUtc)).ToList());
    }

    public async Task<PortResult<DemoBooking>> BookAsync(Guid leadId, DemoSlot slot, string? notes, CancellationToken ct)
    {
        var r = await demos.BookAsync(new BookDemoRequest(leadId, slot.SlotId, null, notes), DemoSource.Automation, ct);
        return r.IsSuccess ? PortResult<DemoBooking>.Ok(new DemoBooking(r.Value.Id.ToString("N"), r.Value.StartUtc, r.Value.EndUtc)) : PortResult<DemoBooking>.Fail(r.Error.Message);
    }
}

/// <summary>The port behind the AI's CreateTrialAccount tool (reached only within the per-lead limit or after approval). It RECORDS the trial; provisioning is the backend's / staff's job and is reported honestly.</summary>
internal sealed class TrialProvisionerPort(ITrialService trials, CrmContext crm) : ITrialProvisioner
{
    public async Task<PortResult<TrialInfo>> CreateAsync(Guid leadId, string? plan, string? notes, CancellationToken ct)
    {
        Guid? planId = null;
        if (!string.IsNullOrWhiteSpace(plan))
        {
            var tenantId = crm.TenantId; var name = plan.Trim();
            planId = await (from p in crm.Db.PricingPlans.AsNoTracking() join pr in crm.Db.CatalogProducts.AsNoTracking() on p.ProductId equals pr.Id
                            where p.TenantId == tenantId && pr.TenantId == tenantId && p.IsActive && p.IsConfirmed && pr.IsActive && pr.IsConfirmed && p.Name == name select (Guid?)p.Id).FirstOrDefaultAsync(ct);
            if (planId is null) return PortResult<TrialInfo>.Fail("There is no confirmed plan with that name. No trial was created.");
        }
        var r = await trials.CreateAsync(new CreateTrialRequest(leadId, planId, null, notes), TrialSource.Automation, ct);
        return r.IsSuccess ? PortResult<TrialInfo>.Ok(new TrialInfo(r.Value.Id.ToString("N"), r.Value.ExpiresAtUtc, r.Value.Status.ToString(), r.Value.ProvisioningMessage)) : PortResult<TrialInfo>.Fail(r.Error.Message);
    }
}

/// <summary>Expands the workspace's own link template. It creates nothing at any meeting provider (no Google Meet / Zoom / Teams call is made).</summary>
internal sealed class TemplateMeetingLinkProvider : IMeetingLinkProvider
{
    public Task<MeetingLink?> ProvideAsync(MeetingLinkRequest request, string? template, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(template)) return Task.FromResult<MeetingLink?>(null);
        var code = Convert.ToHexString(request.DemoId.ToByteArray())[..10].ToLowerInvariant();
        return Task.FromResult<MeetingLink?>(new MeetingLink(template.Replace("{code}", code, StringComparison.Ordinal), "template"));
    }
}
