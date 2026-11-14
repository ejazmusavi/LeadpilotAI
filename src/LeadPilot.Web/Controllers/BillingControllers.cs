using System.Globalization;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Shared.Results;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LeadPilot.Web.Controllers;

/// <summary>The workspace's own subscription: plan, usage against limits, invoices, payments and the upgrade / downgrade flow. Everything is scoped to the signed-in workspace; there is no tenant parameter.</summary>
[Authorize(Policy = PermissionCodes.BillingView)]
public class BillingController(IBillingService billing, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Billing", null));
        return View(new BillingIndexViewModel { Overview = await billing.OverviewAsync(ct), CanManage = await permissions.HasAsync(PermissionCodes.BillingManage, ct) });
    }

    [HttpGet]
    public async Task<IActionResult> Invoices(int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Billing", Url.Action(nameof(Index))), ("Invoices", null));
        return View(new InvoiceListViewModel { Result = await billing.ListInvoicesAsync(page, 20, ct) });
    }

    [HttpGet]
    public async Task<IActionResult> Invoice(Guid id, CancellationToken ct)
    {
        var r = await billing.GetInvoiceAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        Crumbs(("Billing", Url.Action(nameof(Index))), ("Invoices", Url.Action(nameof(Invoices))), (r.Value.Number ?? "Invoice", null));
        return View(new InvoiceViewModel { Invoice = r.Value, CanManage = await permissions.HasAsync(PermissionCodes.BillingManage, ct) });
    }

    /// <summary>Printable invoice (server-rendered HTML, no external service): use the browser's Print → Save as PDF.</summary>
    [HttpGet]
    public async Task<IActionResult> Print(Guid id, CancellationToken ct)
    {
        var r = await billing.GetInvoiceAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        return View(r.Value);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.BillingManage)]
    public async Task<IActionResult> ReportPayment(Guid id, PaymentReportForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid) { Failure("Enter the transfer reference of your payment."); return RedirectToAction(nameof(Invoice), new { id }); }
        var r = await billing.ReportPaymentAsync(id, new PaymentReportRequest(form.EvidenceReference, form.PaidOn, form.Note), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Thank you. The platform will verify your payment; the invoice stays open until then."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Invoice), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Payments(CancellationToken ct)
    {
        Crumbs(("Billing", Url.Action(nameof(Index))), ("Payments", null));
        return View(new BillingPaymentsViewModel { Payments = await billing.ListPaymentsAsync(ct), Changes = await billing.ListPlanChangesAsync(ct) });
    }

    [HttpGet]
    public async Task<IActionResult> Plans(CancellationToken ct)
    {
        Crumbs(("Billing", Url.Action(nameof(Index))), ("Change plan", null));
        var o = await billing.OverviewAsync(ct);
        return View(new PlansViewModel { Plans = await billing.AvailablePlansAsync(ct), Current = o.Entitlements.Subscription, OpenChange = o.OpenChange, CanManage = await permissions.HasAsync(PermissionCodes.BillingManage, ct) });
    }

    [HttpGet, Authorize(Policy = PermissionCodes.BillingManage)]
    public async Task<IActionResult> Preview(Guid planId, CancellationToken ct)
    {
        var r = await billing.PreviewPlanChangeAsync(planId, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Plans)); }
        Crumbs(("Billing", Url.Action(nameof(Index))), ("Change plan", Url.Action(nameof(Plans))), ("Review", null));
        return View(new PlanPreviewViewModel { Preview = r.Value });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.BillingManage)]
    public async Task<IActionResult> RequestChange(Guid planId, CancellationToken ct)
    {
        var r = await billing.RequestPlanChangeAsync(planId, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Plans)); }
        Success(r.Value.Kind == PlanChangeKind.Downgrade ? $"Downgrade scheduled for the end of the current period ({r.Value.EffectiveAtUtc:yyyy-MM-dd})." : r.Value.InvoiceId is not null ? "Upgrade requested. Pay the invoice; the plan changes when the payment is verified." : "Your plan was changed.");
        return r.Value.InvoiceId is { } inv ? RedirectToAction(nameof(Invoice), new { id = inv }) : RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.BillingManage)]
    public async Task<IActionResult> CancelChange(Guid id, CancellationToken ct)
    {
        var r = await billing.CancelPlanChangeAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("The plan change was cancelled."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>The guided checklist of the workspace. Steps complete from real data; each links to the existing page.</summary>
[Authorize(Policy = PermissionCodes.SettingsManage)]
public class OnboardingController(IOnboardingService onboarding) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Getting started", null));
        return View(new OnboardingViewModel { Overview = await onboarding.GetAsync(ct) });
    }
}

/// <summary>Public sign-up. Closed unless the platform owner opens it; rate limited per IP here and per IP/domain in the database; e-mail must be verified to unlock sensitive features.</summary>
[AllowAnonymous]
public class RegisterController(IRegistrationService registration) : AppController
{
    private RegistrationContext Ctx() => new(HttpContext.Connection.RemoteIpAddress?.ToString());

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Open"] = await registration.IsOpenAsync(ct);
        return View(new SignUpViewModel());
    }

    [HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> Index(SignUpViewModel model, CancellationToken ct)
    {
        ViewData["Open"] = await registration.IsOpenAsync(ct);
        if (!ModelState.IsValid) return View(model);
        var r = await registration.RegisterAsync(new RegisterRequest(model.WorkspaceName, model.DisplayName, model.Email, model.Password, model.Timezone, model.Website, model.CaptchaToken), Ctx(), ct);
        if (r.IsFailure) { ModelState.AddModelError(string.Empty, r.Error.Message); model.Password = string.Empty; return View(model); }
        return RedirectToAction(nameof(Sent));
    }

    [HttpGet] public IActionResult Sent() => View();

    [HttpGet]
    public async Task<IActionResult> Verify(Guid userId, string token, CancellationToken ct)
    {
        var r = await registration.VerifyEmailAsync(userId, token, ct);
        ViewData["Ok"] = r.IsSuccess; ViewData["Message"] = r.IsSuccess ? null : r.Error.Message;
        return View();
    }

    [HttpGet] public IActionResult Resend() => View(new ResendViewModel());

    [HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> Resend(ResendViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);
        var r = await registration.ResendVerificationAsync(model.Email, Ctx(), ct);
        if (r.IsFailure) { ModelState.AddModelError(string.Empty, r.Error.Message); return View(model); }
        Success("If that address is waiting for verification, a new link is on its way.");
        return RedirectToAction(nameof(Resend));
    }
}

// ------------------------------------------------------------------ platform administration (Platform.* permissions only)

[Authorize(Policy = PermissionCodes.PlatformTenantsManage)]
public class PlatformTenantsController(IPlatformBillingService platform, IPlanService plans) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(string? q, string? status, int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Platform", null), ("Subscriptions", null));
        TenantStatus? st = Enum.TryParse<TenantStatus>(status, out var s) ? s : null;
        return View(new PlatformTenantsViewModel { Query = q, Status = status, Result = await platform.ListTenantsAsync(q, st, page, 20, ct) });
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var r = await platform.GetTenantAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        Crumbs(("Platform", null), ("Subscriptions", Url.Action(nameof(Index))), (r.Value.Row.Name, null));
        return View(new PlatformTenantViewModel { Detail = r.Value, Plans = await plans.ListAsync(false, ct) });
    }

    private async Task<IActionResult> Done(Guid id, Func<Task<Result<PlatformTenantDetail>>>? work, string ok)
    {
        if (work is null) { Failure("Please fill in the required fields (a reason is mandatory)."); return RedirectToAction(nameof(Details), new { id }); }
        var r = await work();
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(ok); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public Task<IActionResult> Grant(Guid id, GrantForm f, CancellationToken ct) =>
        Done(id, ModelState.IsValid ? () => platform.GrantPlanAsync(id, new GrantPlanRequest(f.PlanId, f.PeriodEnd, f.Reason), ct) : null, "Plan granted without payment (audited).");

    [HttpPost]
    public Task<IActionResult> SetLimit(Guid id, LimitOverrideForm f, CancellationToken ct) =>
        Done(id, ModelState.IsValid ? () => platform.SetLimitOverrideAsync(id, new SetLimitOverrideRequest(f.Dimension, f.Unlimited ? null : f.Value * DimensionCatalog.Get(f.Dimension).UiFactor, f.Unlimited, f.ExpiresAt, f.Reason), ct) : null, "Limit override saved (audited).");

    [HttpPost]
    public Task<IActionResult> SetFeature(Guid id, FeatureOverrideForm f, CancellationToken ct) =>
        Done(id, ModelState.IsValid ? () => platform.SetFeatureOverrideAsync(id, new SetFeatureOverrideRequest(f.Feature, f.Enabled, f.ExpiresAt, f.Reason), ct) : null, "Feature override saved (audited).");

    [HttpPost]
    public Task<IActionResult> RemoveOverride(Guid id, string key, CancellationToken ct) => Done(id, () => platform.RemoveOverrideAsync(id, key, ct), "Override removed.");

    [HttpPost]
    public Task<IActionResult> Suspend(Guid id, SuspendForm f, CancellationToken ct) =>
        Done(id, ModelState.IsValid ? () => platform.SuspendAsync(id, new SuspendTenantBillingRequest(f.Reason, f.Kind), ct) : null, "Tenant suspended: sign-in, API, sending and jobs are blocked.");

    [HttpPost]
    public Task<IActionResult> Reactivate(Guid id, CancellationToken ct) => Done(id, () => platform.ReactivateAsync(id, ct), "Tenant reactivated.");
}

[Authorize(Policy = PermissionCodes.PlatformPlansManage)]
public class PlatformPlansController(IPlanService plans) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Platform", null), ("Plans", null));
        return View(new PlatformPlansViewModel { Plans = await plans.ListAsync(true, ct) });
    }

    [HttpGet]
    public IActionResult Create()
    {
        Crumbs(("Platform", null), ("Plans", Url.Action(nameof(Index))), ("New plan", null));
        return View("Edit", new PlatformPlanForm());
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var r = await plans.GetAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        var p = r.Value;
        Crumbs(("Platform", null), ("Plans", Url.Action(nameof(Index))), (p.Name, null));
        return View(new PlatformPlanForm
        {
            Id = p.Id, Code = p.Code, Name = p.Name, Description = p.Description, Cycle = p.Cycle, CustomPeriodDays = p.CustomPeriodDays, Price = p.Price, Currency = p.Currency, IsTrial = p.IsTrial, TrialDays = p.TrialDays,
            Visibility = p.Visibility, SortOrder = p.SortOrder, Features = p.Features.ToList(),
            Limits = p.Limits.ToDictionary(l => l.Key, l => l.Value is null ? (long?)null : l.Value / DimensionCatalog.Get(l.Key).UiFactor),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Save(PlatformPlanForm m, CancellationToken ct)
    {
        Crumbs(("Platform", null), ("Plans", Url.Action(nameof(Index))), (m.Id is null ? "New plan" : m.Name, null));
        if (!ModelState.IsValid) return View("Edit", m);
        var limits = m.Limits.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => (long?)(kv.Value!.Value * DimensionCatalog.Get(kv.Key).UiFactor));
        var req = new SavePlanRequest(m.Code, m.Name, m.Description, m.Cycle, m.CustomPeriodDays, m.Price, m.Currency, m.IsTrial, m.TrialDays, m.Visibility, m.SortOrder, limits, m.Features ?? []);
        var r = m.Id is { } id ? await plans.UpdateAsync(id, req, ct) : await plans.CreateAsync(req, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { AddError(r); return View("Edit", m); }
        Success("Plan saved. Tenants on this plan use the new limits immediately.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
    {
        var r = await plans.ArchiveAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Plan archived."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var r = await plans.RestoreAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Plan restored."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>The manual payment verification queue (invoices waiting for money, reported ones first) and the platform invoice list.</summary>
[Authorize(Policy = PermissionCodes.PlatformBillingVerify)]
public class PlatformPaymentsController(IPlatformBillingService platform) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Platform", null), ("Payment verification", null));
        return View(new QueueViewModel { Result = await platform.VerificationQueueAsync(page, 20, ct), Queue = true });
    }

    [HttpGet]
    public async Task<IActionResult> Invoices(string? status, int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Platform", null), ("All invoices", null));
        InvoiceStatus? st = Enum.TryParse<InvoiceStatus>(status, out var s) ? s : null;
        return View("Index", new QueueViewModel { Result = await platform.InvoicesAsync(st, page, 20, ct), Status = status });
    }

    [HttpGet]
    public async Task<IActionResult> Invoice(Guid forTenant, Guid id, CancellationToken ct)
    {
        var r = await platform.GetInvoiceAsync(forTenant, id, ct);
        if (Reject(r) is { } rej) return rej;
        Crumbs(("Platform", null), ("Payment verification", Url.Action(nameof(Index))), (r.Value.Number ?? "Invoice", null));
        return View(new PlatformInvoiceViewModel { Invoice = r.Value, Form = new VerifyForm { ForTenant = forTenant, Amount = r.Value.Total, Currency = r.Value.Currency } });
    }

    [HttpPost]
    public async Task<IActionResult> Verify(Guid id, VerifyForm f, CancellationToken ct)
    {
        if (!ModelState.IsValid) { Failure("Fill in the amount, currency, method and the evidence reference (receipt or transfer id)."); return RedirectToAction(nameof(Invoice), new { forTenant = f.ForTenant, id }); }
        var r = await platform.VerifyPaymentAsync(f.ForTenant, id, new VerifyBillingPaymentRequest(f.Amount, f.Currency, f.Method, f.EvidenceReference, f.PaidAt, f.Notes), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Payment verified. The subscription was activated or renewed."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Invoice), new { forTenant = f.ForTenant, id });
    }

    [HttpPost]
    public async Task<IActionResult> Void(Guid forTenant, Guid id, string reason, CancellationToken ct)
    {
        var r = await platform.VoidInvoiceAsync(forTenant, id, reason, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Invoice voided."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Invoice), new { forTenant, id });
    }
}

[Authorize(Policy = PermissionCodes.PlatformSettingsManage)]
public class PlatformSettingsController(IPlatformSettings settings, IPlanService plans) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Platform", null), ("Settings", null));
        return View(new PlatformSettingsViewModel { Settings = await settings.GetAllAsync(ct), Plans = await plans.ListAsync(false, ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Index(PlatformSettingsDto s, CancellationToken ct)
    {
        var r = await settings.SaveAsync(s, ct);
        if (r.IsSuccess) Success("Platform settings saved (audited)."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}

[Authorize(Policy = PermissionCodes.PlatformAuditView)]
public class PlatformAuditController(IPlatformBillingService platform) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(string? action, int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Platform", null), ("Platform audit", null));
        return View(new PlatformAuditViewModel { Action = action, Result = await platform.AuditAsync(action, page, 50, ct) });
    }
}
