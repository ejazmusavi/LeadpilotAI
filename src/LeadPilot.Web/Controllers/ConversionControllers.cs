using LeadPilot.Application.Contracts;
using System.Globalization;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Conversion;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Conversion;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

internal static class ConversionWeb
{
    public static async Task<List<PlanOption>> PlansAsync(ICatalogService catalog, CancellationToken ct)
    {
        var list = new List<PlanOption>();
        foreach (var p in await catalog.ListAsync(ct))
        {
            if (!p.IsActive) continue;
            var d = await catalog.GetAsync(p.Id, ct);
            if (d.IsFailure) continue;
            list.AddRange(d.Value.Plans.Where(x => x.IsActive).Select(x => new PlanOption(x.Id, $"{d.Value.Name} — {x.Name}" + (x.Price is null ? " (no price)" : $" ({x.Price:0.00} {x.Currency})") + (x.IsConfirmed ? "" : " [not confirmed]"), x.Price, x.Currency)));
        }
        return list;
    }

    /// <summary>The UTC range of a local calendar period in the workspace time zone (DST-safe: both ends are converted, the length is whatever elapsed).</summary>
    public static (DateTime FromUtc, DateTime ToUtc) Range(string zoneId, DateTime fromLocal, int days)
    {
        var tz = SlotCalculator.FindZone(zoneId);
        return (SlotCalculator.ToUtc(tz, fromLocal.Date), SlotCalculator.ToUtc(tz, fromLocal.Date.AddDays(days)));
    }

    public static int ParseMinutes(string? hhmm) => TimeSpan.TryParseExact(hhmm?.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var t) || TimeSpan.TryParseExact(hhmm?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out t) ? (int)t.TotalMinutes : -1;
}

[Authorize(Policy = PermissionCodes.DemosView)]
public class DemosController(IDemoService demos, IConversionSettingsService settings, ILeadService leads, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(string? view, DateTime? date, Guid? repUserId, CancellationToken ct)
    {
        Crumbs(("Sales conversion", null), ("Demo calendar", null));
        var s = await settings.GetAsync(ct); var tz = SlotCalculator.FindZone(s.TimeZoneId);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
        var day = (date ?? today).Date; var isDay = string.Equals(view, "day", StringComparison.OrdinalIgnoreCase);
        var from = isDay ? day : day.AddDays(-(((int)day.DayOfWeek + 6) % 7)); var days = isDay ? 1 : 7;
        var (fromUtc, toUtc) = ConversionWeb.Range(s.TimeZoneId, from, days);
        var list = await demos.ListAsync(new DemoQuery { FromUtc = fromUtc, ToUtc = toUtc, RepUserId = repUserId, PageSize = 200 }, ct);
        var reps = await settings.RepsAsync(ct);
        return View(new CalendarViewModel
        {
            View = isDay ? "day" : "week", Date = day, FromLocal = from, Days = days, TimeZoneId = s.TimeZoneId, RepUserId = repUserId, Demos = list.Items, Reps = reps,
            Slots = await demos.SlotsAsync(fromUtc, toUtc, repUserId, ct), CanBook = await permissions.HasAsync(PermissionCodes.DemosBook, ct), Configured = reps.Any(r => r.IsActive && r.Rules.Count > 0),
        });
    }

    [HttpGet, Authorize(Policy = PermissionCodes.DemosBook)]
    public async Task<IActionResult> Book(Guid? leadId, string? search, DateTime? date, string? slotId, CancellationToken ct)
    {
        Crumbs(("Sales conversion", null), ("Demo calendar", Url.Action(nameof(Index))), ("Book a demo", null));
        var s = await settings.GetAsync(ct); var tz = SlotCalculator.FindZone(s.TimeZoneId);
        var day = (date ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz)).Date;
        var m = new BookDemoViewModel { LeadId = leadId, Search = search, Date = day, TimeZoneId = s.TimeZoneId, SlotId = slotId, Reps = await settings.RepsAsync(ct) };
        if (leadId is { } lid) { var l = await leads.GetAsync(lid, ct); if (l.IsSuccess) m.LeadName = l.Value.Name; else m.LeadId = null; }
        if (m.LeadId is null && !string.IsNullOrWhiteSpace(search)) m.Leads = (await leads.ListAsync(new LeadQuery { Search = search, PageSize = 10 }, ct)).Items;
        var (a, b) = ConversionWeb.Range(s.TimeZoneId, day, 1);
        m.Slots = await demos.SlotsAsync(a, b, null, ct);
        return View(m);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DemosBook)]
    public async Task<IActionResult> Book(BookDemoViewModel m, CancellationToken ct)
    {
        if (m.LeadId is null || string.IsNullOrWhiteSpace(m.SlotId)) { Failure("Choose a lead and a slot."); return RedirectToAction(nameof(Book), new { leadId = m.LeadId, date = m.Date }); }
        var r = await demos.BookAsync(new BookDemoRequest(m.LeadId.Value, m.SlotId, null, Pick(m.Notes, 500), Pick(m.MeetingLink, 500), m.PreferredRepUserId), DemoSource.Staff, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Book), new { leadId = m.LeadId, date = m.Date }); }
        Success("Demo booked.");
        return RedirectToAction(nameof(Details), new { id = r.Value.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, bool reschedule, DateTime? date, CancellationToken ct)
    {
        var r = await demos.GetAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        Crumbs(("Sales conversion", null), ("Demo calendar", Url.Action(nameof(Index))), ($"Demo · {r.Value.LeadName}", null));
        var m = new DemoDetailsViewModel { Demo = r.Value, CanBook = await permissions.HasAsync(PermissionCodes.DemosBook, ct), Rescheduling = reschedule };
        if (reschedule)
        {
            var s = await settings.GetAsync(ct);
            m.Date = (date ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SlotCalculator.FindZone(s.TimeZoneId))).Date;
            var (a, b) = ConversionWeb.Range(s.TimeZoneId, m.Date, 1);
            m.Slots = await demos.SlotsAsync(a, b, null, ct); m.Reps = await settings.RepsAsync(ct);
        }
        return View(m);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DemosBook)]
    public Task<IActionResult> Reschedule(Guid id, string slotId, Guid? preferredRepUserId, CancellationToken ct) =>
        Do(id, async () => await demos.RescheduleAsync(id, new RescheduleDemoRequest(slotId ?? "", preferredRepUserId), ct), "Demo rescheduled.");

    [HttpPost, Authorize(Policy = PermissionCodes.DemosBook)]
    public Task<IActionResult> Cancel(Guid id, string? reason, CancellationToken ct) => Do(id, async () => await demos.CancelAsync(id, new CancelDemoRequest(Pick(reason, 300)), ct), "Demo cancelled.");

    [HttpPost, Authorize(Policy = PermissionCodes.DemosBook)]
    public Task<IActionResult> Accept(Guid id, CancellationToken ct) => Do(id, async () => await demos.AcceptAsync(id, ct), "Demo request accepted.");

    [HttpPost, Authorize(Policy = PermissionCodes.DemosBook)]
    public Task<IActionResult> Confirm(Guid id, CancellationToken ct) => Do(id, async () => await demos.ConfirmAsync(id, ct), "Demo confirmed.");

    [HttpPost, Authorize(Policy = PermissionCodes.DemosBook)]
    public Task<IActionResult> Complete(Guid id, CancellationToken ct) => Do(id, async () => await demos.CompleteAsync(id, ct), "Demo marked as completed.");

    [HttpPost, Authorize(Policy = PermissionCodes.DemosBook)]
    public Task<IActionResult> NoShow(Guid id, CancellationToken ct) => Do(id, async () => await demos.NoShowAsync(id, ct), "Demo marked as no-show.");

    [HttpPost, Authorize(Policy = PermissionCodes.DemosBook)]
    public Task<IActionResult> Edit(Guid id, string? meetingLink, string? notes, CancellationToken ct) => Do(id, async () => await demos.UpdateAsync(id, new UpdateDemoRequest(Pick(meetingLink, 500), Pick(notes, 500)), ct), "Demo updated.");

    private async Task<IActionResult> Do(Guid id, Func<Task<Shared.Results.Result<DemoDto>>> action, string ok)
    {
        var r = await action();
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(ok); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }
}

[Authorize(Policy = PermissionCodes.DemosView)]
public class DemoSettingsController(IConversionSettingsService settings, IChannelAccountService accounts, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Sales conversion", null), ("Demo, trial and payment settings", null));
        return View(new DemoSettingsViewModel
        {
            Settings = await settings.GetAsync(ct), Reps = await settings.RepsAsync(ct), Candidates = await settings.CandidateRepsAsync(ct),
            Accounts = (await accounts.ListAsync(ct)).Select(a => new LookupItem(a.Id, a.DisplayName)).ToList(), CanManage = await permissions.HasAsync(PermissionCodes.DemosManage, ct),
        });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DemosManage)]
    public async Task<IActionResult> Save(SaveConversionSettingsRequest m, CancellationToken ct)
    {
        var r = await settings.SaveAsync(m with { TenantId = null }, ct);
        if (r.IsSuccess) Success("Settings saved."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DemosManage)]
    public async Task<IActionResult> SaveRep(Guid userId, bool isActive, string? timeZoneId, int bufferMinutes, int maxPerDay, CancellationToken ct)
    {
        var r = await settings.SaveRepAsync(new SaveRepRequest(userId, isActive, Pick(timeZoneId, 64), bufferMinutes, maxPerDay), ct);
        if (r.IsSuccess) Success("Representative saved."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Hours(Guid id, CancellationToken ct)
    {
        var r = await settings.GetRepAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        Crumbs(("Sales conversion", null), ("Settings", Url.Action(nameof(Index))), ($"Hours · {r.Value.UserName}", null));
        return View(new RepHoursViewModel { Rep = r.Value, CanManage = await permissions.HasAsync(PermissionCodes.DemosManage, ct) });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DemosManage)]
    public async Task<IActionResult> Hours(Guid id, Dictionary<string, string?> p, CancellationToken ct)
    {
        var rules = new List<RuleDto>();
        foreach (var day in Enum.GetValues<DayOfWeek>())
            for (var i = 1; i <= 2; i++)
            {
                p.TryGetValue($"d{(int)day}s{i}", out var s); p.TryGetValue($"d{(int)day}e{i}", out var e);
                if (string.IsNullOrWhiteSpace(s) && string.IsNullOrWhiteSpace(e)) continue;
                var a = ConversionWeb.ParseMinutes(s); var b = ConversionWeb.ParseMinutes(e);
                if (a < 0 || b < 0) { Failure($"Use HH:mm for {day} (for example 09:00)."); return RedirectToAction(nameof(Hours), new { id }); }
                rules.Add(new RuleDto(day, a, b));
            }
        var r = await settings.SaveRulesAsync(id, new SaveRulesRequest(rules), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Weekly hours saved."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Hours), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DemosManage)]
    public async Task<IActionResult> AddException(Guid id, DateTime date, ExceptionKind kind, string? start, string? end, string? note, CancellationToken ct)
    {
        int? a = string.IsNullOrWhiteSpace(start) ? null : ConversionWeb.ParseMinutes(start); int? b = string.IsNullOrWhiteSpace(end) ? null : ConversionWeb.ParseMinutes(end);
        if (a < 0 || b < 0) { Failure("Use HH:mm for the times, or leave both empty for the whole day."); return RedirectToAction(nameof(Hours), new { id }); }
        var r = await settings.AddExceptionAsync(id, new AddExceptionRequest(date, kind, a, b, Pick(note, 200)), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Exception added."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Hours), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DemosManage)]
    public async Task<IActionResult> RemoveException(Guid id, Guid exceptionId, CancellationToken ct)
    {
        var r = await settings.RemoveExceptionAsync(id, exceptionId, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Exception removed."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Hours), new { id });
    }
}

[Authorize(Policy = PermissionCodes.TrialsView)]
public class TrialsController(ITrialService trials, ILeadService leads, ICatalogService catalog, IConversionSettingsService settings, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(TrialQuery query, CancellationToken ct)
    {
        Crumbs(("Sales conversion", null), ("Trials", null));
        return View(new TrialsViewModel { Result = await trials.ListAsync(query, ct), Query = query, QueryValues = new Dictionary<string, string?> { ["status"] = query.Status?.ToString() }, CanManage = await permissions.HasAsync(PermissionCodes.TrialsManage, ct) });
    }

    [HttpGet, Authorize(Policy = PermissionCodes.TrialsManage)]
    public async Task<IActionResult> Create(Guid? leadId, string? search, CancellationToken ct)
    {
        Crumbs(("Sales conversion", null), ("Trials", Url.Action(nameof(Index))), ("New trial", null));
        var m = new TrialCreateViewModel { LeadId = leadId, Search = search, Plans = await ConversionWeb.PlansAsync(catalog, ct), DefaultDays = (await settings.GetAsync(ct)).TrialDefaultDays };
        if (leadId is { } id) { var l = await leads.GetAsync(id, ct); if (l.IsSuccess) m.LeadName = l.Value.Name; else m.LeadId = null; }
        if (m.LeadId is null && !string.IsNullOrWhiteSpace(search)) m.Leads = (await leads.ListAsync(new LeadQuery { Search = search, PageSize = 10 }, ct)).Items;
        return View(m);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.TrialsManage)]
    public async Task<IActionResult> Create(TrialCreateViewModel m, CancellationToken ct)
    {
        if (m.LeadId is null) { Failure("Choose a lead."); return RedirectToAction(nameof(Create)); }
        var r = await trials.CreateAsync(new CreateTrialRequest(m.LeadId.Value, m.PlanId, m.DurationDays, Pick(m.Notes, 500)), TrialSource.Staff, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Create), new { leadId = m.LeadId }); }
        Success("Trial recorded. " + r.Value.ProvisioningMessage);
        return RedirectToAction(nameof(Details), new { id = r.Value.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var r = await trials.GetAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        Crumbs(("Sales conversion", null), ("Trials", Url.Action(nameof(Index))), (r.Value.LeadName, null));
        return View(new TrialDetailsViewModel { Trial = r.Value, CanManage = await permissions.HasAsync(PermissionCodes.TrialsManage, ct) });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.TrialsManage)]
    public Task<IActionResult> Provisioned(Guid id, string? externalAccountRef, string? loginUrl, string? note, CancellationToken ct) =>
        Do(id, () => trials.MarkProvisionedAsync(id, new ProvisionTrialRequest(Pick(externalAccountRef, 150), Pick(loginUrl, 500), Pick(note, 500)), ct), "Marked as provisioned: the trial period has started.");

    [HttpPost, Authorize(Policy = PermissionCodes.TrialsManage)]
    public Task<IActionResult> Extend(Guid id, int days, CancellationToken ct) => Do(id, () => trials.ExtendAsync(id, new ExtendTrialRequest(days), ct), "Trial extended.");

    [HttpPost, Authorize(Policy = PermissionCodes.TrialsManage)]
    public Task<IActionResult> Cancel(Guid id, string? reason, CancellationToken ct) => Do(id, () => trials.CancelAsync(id, new CancelTrialRequest(Pick(reason, 300)), ct), "Trial cancelled.");

    [HttpPost, Authorize(Policy = PermissionCodes.TrialsManage)]
    public Task<IActionResult> Usage(Guid id, DateTime? lastActivityUtc, int loginCount, int activeUsers, string? notes, CancellationToken ct) =>
        Do(id, () => trials.RecordUsageAsync(id, new RecordTrialUsageRequest(lastActivityUtc, loginCount, activeUsers, Pick(notes, 500)), ct), "Usage recorded.");

    private async Task<IActionResult> Do(Guid id, Func<Task<Shared.Results.Result<TrialDto>>> action, string ok)
    {
        var r = await action();
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(ok); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }
}

[Authorize(Policy = PermissionCodes.CustomersView)]
public class CustomersController(ICustomerService customers, ITrialService trials, ICatalogService catalog, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CustomerQuery query, CancellationToken ct)
    {
        Crumbs(("Sales conversion", null), ("Customers", null));
        return View(new CustomersViewModel { Result = await customers.ListAsync(query, ct), Query = query, QueryValues = new Dictionary<string, string?> { ["search"] = query.Search, ["status"] = query.Status?.ToString() },
            Won = await customers.WonOpportunitiesAsync(ct), CanManage = await permissions.HasAsync(PermissionCodes.CustomersManage, ct) });
    }

    [HttpGet, Authorize(Policy = PermissionCodes.CustomersManage)]
    public async Task<IActionResult> Convert(Guid opportunityId, CancellationToken ct)
    {
        var won = (await customers.WonOpportunitiesAsync(ct)).FirstOrDefault(w => w.OpportunityId == opportunityId);
        if (won is null) { Failure("That opportunity is not a Won opportunity waiting for conversion."); return RedirectToAction(nameof(Index)); }
        Crumbs(("Sales conversion", null), ("Customers", Url.Action(nameof(Index))), ("Convert to customer", null));
        var t = await trials.ListAsync(new TrialQuery { LeadId = won.LeadId, PageSize = 20 }, ct);
        return View(new ConvertViewModel { OpportunityId = opportunityId, LeadName = won.LeadName, OpportunityName = won.Name, OpportunityValue = won.Value, OpportunityCurrency = won.Currency, Plans = await ConversionWeb.PlansAsync(catalog, ct),
            Trials = t.Items.Where(x => x.Status is TrialStatus.Active or TrialStatus.Expired).ToList() });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CustomersManage)]
    public async Task<IActionResult> Convert(ConvertViewModel m, CancellationToken ct)
    {
        var r = await customers.ConvertAsync(new ConvertOpportunityRequest(m.OpportunityId, m.PlanId, m.Terms ?? "", m.BillingCycle, m.Price, Pick(m.Currency, 3), m.TrialId), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Convert), new { opportunityId = m.OpportunityId }); }
        Success($"Converted into customer {r.Value.Code}. The subscription is pending until a payment is verified.");
        return RedirectToAction(nameof(Details), new { id = r.Value.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var r = await customers.GetAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        Crumbs(("Sales conversion", null), ("Customers", Url.Action(nameof(Index))), (r.Value.Name, null));
        return View(new CustomerDetailsViewModel { Customer = r.Value, CanManage = await permissions.HasAsync(PermissionCodes.CustomersManage, ct), CanRequest = await permissions.HasAsync(PermissionCodes.PaymentsRequest, ct), CanVerify = await permissions.HasAsync(PermissionCodes.PaymentsVerify, ct) });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CustomersManage)]
    public async Task<IActionResult> Account(Guid id, string? accountReference, string? accountNotes, CancellationToken ct)
    {
        var r = await customers.SetAccountAsync(id, new SetAccountRequest(Pick(accountReference, 150), Pick(accountNotes, 500)), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Account details saved."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CustomersManage)]
    public async Task<IActionResult> CancelSubscription(Guid id, Guid subscriptionId, string? reason, CancellationToken ct)
    {
        var r = await customers.CancelSubscriptionAsync(id, subscriptionId, new CancelSubscriptionRequest(Pick(reason, 300)), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Subscription cancelled."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }
}

[Authorize(Policy = PermissionCodes.CustomersView)]
public class PaymentsController(IPaymentService payments, IPaymentProviderService providers, IPermissionEvaluator permissions, Microsoft.Extensions.Configuration.IConfiguration config) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(PaymentRequestQuery query, CancellationToken ct)
    {
        Crumbs(("Sales conversion", null), ("Payments", null));
        return View(new PaymentsViewModel { Result = await payments.ListAsync(query, ct), Query = query, QueryValues = new Dictionary<string, string?> { ["status"] = query.Status?.ToString() },
            CanRequest = await permissions.HasAsync(PermissionCodes.PaymentsRequest, ct), CanVerify = await permissions.HasAsync(PermissionCodes.PaymentsVerify, ct) });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.PaymentsRequest)]
    public async Task<IActionResult> Create(Guid customerId, Guid subscriptionId, int? dueInDays, string? instructions, CancellationToken ct)
    {
        var r = await payments.CreateAsync(new CreatePaymentRequestRequest(subscriptionId, dueInDays, Pick(instructions, 1500)), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success($"Payment request {r.Value.Reference} created. Nothing is charged; the subscription stays pending until a payment is verified."); else Failure(r.Error.Message);
        return RedirectToAction("Details", "Customers", new { id = customerId });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.PaymentsRequest)]
    public async Task<IActionResult> Cancel(Guid id, Guid customerId, string? reason, CancellationToken ct)
    {
        var r = await payments.CancelAsync(id, new CancelPaymentRequestRequest(Pick(reason, 300)), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Payment request cancelled."); else Failure(r.Error.Message);
        return RedirectToAction("Details", "Customers", new { id = customerId });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.PaymentsVerify)]
    public async Task<IActionResult> Verify(Guid id, Guid customerId, decimal amount, string currency, PaymentMethod method, string evidenceReference, DateTime? paidAtUtc, string? notes, CancellationToken ct)
    {
        var r = await payments.VerifyManuallyAsync(id, new VerifyPaymentRequest(amount, currency ?? "", method, evidenceReference ?? "", paidAtUtc, Pick(notes, 500)), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Payment verified and the subscription is now active."); else Failure(r.Error.Message);
        return RedirectToAction("Details", "Customers", new { id = customerId });
    }

    [HttpGet, Authorize(Policy = PermissionCodes.PaymentsVerify)]
    public async Task<IActionResult> Providers(CancellationToken ct)
    {
        Crumbs(("Sales conversion", null), ("Payments", Url.Action(nameof(Index))), ("Payment providers", null));
        return View(new ProvidersViewModel { Installed = providers.InstalledAdapters(), Configs = await providers.ListAsync(ct), WebhookBase = (config["Messaging:PublicWebhookBaseUrl"] ?? "").TrimEnd('/') });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.PaymentsVerify)]
    public async Task<IActionResult> Providers(string providerKey, string secret, bool isActive, CancellationToken ct)
    {
        var r = await providers.SaveAsync(new SaveProviderConfigRequest(providerKey ?? "", secret ?? "", isActive), ct);
        if (r.IsSuccess) Success("Provider webhook saved. The secret is stored encrypted and is not shown again."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Providers));
    }
}
