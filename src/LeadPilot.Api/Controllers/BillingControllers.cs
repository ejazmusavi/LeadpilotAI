using LeadPilot.Application.Billing;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LeadPilot.Api.Controllers;

/// <summary>
/// The signed-in workspace's own subscription. Limit and feature refusals anywhere in the API answer <b>402</b> with <c>code</c> = <c>plan_limit_exceeded</c> or <c>feature_not_in_plan</c> (a typed, machine-readable
/// error with a human message); an unverified self-registered workspace gets <c>403 email_not_verified</c> for sensitive features. There is no tenant parameter: the tenant is the token's.
/// </summary>
[Route("api/v1/billing")]
public class BillingController(IBillingService billing, IOnboardingService onboarding) : ApiControllerBase
{
    /// <summary>Subscription, usage vs limits per dimension, features, threshold notices (80 % / 100 %), open invoices and the open plan change.</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.BillingView)]
    public async Task<BillingOverview> Overview(CancellationToken ct) => await billing.OverviewAsync(ct);

    [HttpGet("invoices"), Authorize(Policy = PermissionCodes.BillingView)]
    public async Task<IActionResult> Invoices([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) => Ok(await billing.ListInvoicesAsync(page, pageSize, ct));

    [HttpGet("invoices/{id:guid}"), Authorize(Policy = PermissionCodes.BillingView)]
    public async Task<IActionResult> Invoice(Guid id, CancellationToken ct) => Respond(await billing.GetInvoiceAsync(id, ct));

    /// <summary>Tell the platform you paid an invoice. It is NOT a payment until an operator verifies it.</summary>
    [HttpPost("invoices/{id:guid}/payment-report"), Authorize(Policy = PermissionCodes.BillingManage)]
    public async Task<IActionResult> ReportPayment(Guid id, PaymentReportRequest request, CancellationToken ct) => Respond(await billing.ReportPaymentAsync(id, request, ct));

    [HttpGet("payments"), Authorize(Policy = PermissionCodes.BillingView)]
    public async Task<IActionResult> Payments(CancellationToken ct) => Ok(await billing.ListPaymentsAsync(ct));

    [HttpGet("plans"), Authorize(Policy = PermissionCodes.BillingView)]
    public async Task<IActionResult> Plans(CancellationToken ct) => Ok(await billing.AvailablePlansAsync(ct));

    /// <summary>What a change to the plan would cost / credit, and for a downgrade which limits the current usage exceeds.</summary>
    [HttpGet("plans/{planId:guid}/preview"), Authorize(Policy = PermissionCodes.BillingManage)]
    public async Task<IActionResult> Preview(Guid planId, CancellationToken ct) => Respond(await billing.PreviewPlanChangeAsync(planId, ct));

    /// <summary>Upgrade (invoice, effective when its payment is verified) or downgrade (scheduled for the period end; 409 with the report when usage exceeds the target plan).</summary>
    [HttpPost("plan-changes"), Authorize(Policy = PermissionCodes.BillingManage)]
    public async Task<IActionResult> RequestChange(PlanChangeRequestBody request, CancellationToken ct) => Respond(await billing.RequestPlanChangeAsync(request.PlanId, ct));

    [HttpGet("plan-changes"), Authorize(Policy = PermissionCodes.BillingView)]
    public async Task<IActionResult> Changes(CancellationToken ct) => Ok(await billing.ListPlanChangesAsync(ct));

    [HttpDelete("plan-changes/{id:guid}"), Authorize(Policy = PermissionCodes.BillingManage)]
    public async Task<IActionResult> CancelChange(Guid id, CancellationToken ct) => Respond(await billing.CancelPlanChangeAsync(id, ct));

    /// <summary>The onboarding checklist, computed from the workspace's real data.</summary>
    [HttpGet("/api/v1/onboarding"), Authorize(Policy = PermissionCodes.SettingsManage)]
    public async Task<OnboardingOverview> Onboarding(CancellationToken ct) => await onboarding.GetAsync(ct);
}

/// <summary>Public self-registration (anonymous). 403 <c>registration_closed</c> unless the platform owner opened it; 429 when rate limited; the answer for an address that already exists is identical to a success.</summary>
[Route("api/v1/registration"), AllowAnonymous]
public class RegistrationController(IRegistrationService registration) : ApiControllerBase
{
    public sealed record VerifyEmailBody(Guid UserId, string Token);
    public sealed record ResendBody(string Email);

    [HttpGet("status")]
    public async Task<object> Status(CancellationToken ct) => new { open = await registration.IsOpenAsync(ct) };

    [HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct) =>
        Respond(await registration.RegisterAsync(request, new RegistrationContext(HttpContext.Connection.RemoteIpAddress?.ToString()), ct));

    [HttpPost("verify-email"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Verify(VerifyEmailBody body, CancellationToken ct) => Respond(await registration.VerifyEmailAsync(body.UserId, body.Token, ct));

    [HttpPost("resend"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Resend(ResendBody body, CancellationToken ct) =>
        Respond(await registration.ResendVerificationAsync(body.Email, new RegistrationContext(HttpContext.Connection.RemoteIpAddress?.ToString()), ct));
}

/// <summary>Platform administration. Every endpoint needs a Platform.* permission, which only roles of the platform tenant can hold: a workspace token always gets 403.</summary>
[Route("api/v1/platform")]
public class PlatformController(IPlanService plans, IPlatformBillingService platform, IPlatformSettings settings) : ApiControllerBase
{
    // ---- plans
    [HttpGet("plans"), Authorize(Policy = PermissionCodes.PlatformPlansManage)]
    public async Task<IActionResult> Plans([FromQuery] bool includeArchived = true, CancellationToken ct = default) => Ok(await plans.ListAsync(includeArchived, ct));

    [HttpGet("plans/{id:guid}"), Authorize(Policy = PermissionCodes.PlatformPlansManage)]
    public async Task<IActionResult> Plan(Guid id, CancellationToken ct) => Respond(await plans.GetAsync(id, ct));

    [HttpPost("plans"), Authorize(Policy = PermissionCodes.PlatformPlansManage)]
    public async Task<IActionResult> CreatePlan(SavePlanRequest request, CancellationToken ct)
    {
        var r = await plans.CreateAsync(request, ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Plan), new { id = r.Value.Id }, r.Value) : Fail(r);
    }

    [HttpPut("plans/{id:guid}"), Authorize(Policy = PermissionCodes.PlatformPlansManage)]
    public async Task<IActionResult> UpdatePlan(Guid id, SavePlanRequest request, CancellationToken ct) => Respond(await plans.UpdateAsync(id, request, ct));

    [HttpPost("plans/{id:guid}/archive"), Authorize(Policy = PermissionCodes.PlatformPlansManage)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct) => Respond(await plans.ArchiveAsync(id, ct));

    [HttpPost("plans/{id:guid}/restore"), Authorize(Policy = PermissionCodes.PlatformPlansManage)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct) => Respond(await plans.RestoreAsync(id, ct));

    // ---- tenants
    [HttpGet("tenants"), Authorize(Policy = PermissionCodes.PlatformTenantsManage)]
    public async Task<IActionResult> Tenants([FromQuery] string? search, [FromQuery] TenantStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await platform.ListTenantsAsync(search, status, page, pageSize, ct));

    [HttpGet("tenants/{id:guid}"), Authorize(Policy = PermissionCodes.PlatformTenantsManage)]
    public async Task<IActionResult> Tenant(Guid id, CancellationToken ct) => Respond(await platform.GetTenantAsync(id, ct));

    [HttpPost("tenants/{id:guid}/grant"), Authorize(Policy = PermissionCodes.PlatformTenantsManage)]
    public async Task<IActionResult> Grant(Guid id, GrantPlanRequest request, CancellationToken ct) => Respond(await platform.GrantPlanAsync(id, request, ct));

    [HttpPut("tenants/{id:guid}/overrides/limits"), Authorize(Policy = PermissionCodes.PlatformTenantsManage)]
    public async Task<IActionResult> SetLimit(Guid id, SetLimitOverrideRequest request, CancellationToken ct) => Respond(await platform.SetLimitOverrideAsync(id, request, ct));

    [HttpPut("tenants/{id:guid}/overrides/features"), Authorize(Policy = PermissionCodes.PlatformTenantsManage)]
    public async Task<IActionResult> SetFeature(Guid id, SetFeatureOverrideRequest request, CancellationToken ct) => Respond(await platform.SetFeatureOverrideAsync(id, request, ct));

    [HttpDelete("tenants/{id:guid}/overrides/{key}"), Authorize(Policy = PermissionCodes.PlatformTenantsManage)]
    public async Task<IActionResult> RemoveOverride(Guid id, string key, CancellationToken ct) => Respond(await platform.RemoveOverrideAsync(id, key, ct));

    [HttpPost("tenants/{id:guid}/suspend"), Authorize(Policy = PermissionCodes.PlatformTenantsManage)]
    public async Task<IActionResult> Suspend(Guid id, SuspendTenantBillingRequest request, CancellationToken ct) => Respond(await platform.SuspendAsync(id, request, ct));

    [HttpPost("tenants/{id:guid}/reactivate"), Authorize(Policy = PermissionCodes.PlatformTenantsManage)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct) => Respond(await platform.ReactivateAsync(id, ct));

    // ---- payments
    [HttpGet("payments/queue"), Authorize(Policy = PermissionCodes.PlatformBillingVerify)]
    public async Task<IActionResult> Queue([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) => Ok(await platform.VerificationQueueAsync(page, pageSize, ct));

    [HttpGet("invoices"), Authorize(Policy = PermissionCodes.PlatformBillingVerify)]
    public async Task<IActionResult> Invoices([FromQuery] InvoiceStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) => Ok(await platform.InvoicesAsync(status, page, pageSize, ct));

    [HttpGet("tenants/{id:guid}/invoices/{invoiceId:guid}"), Authorize(Policy = PermissionCodes.PlatformBillingVerify)]
    public async Task<IActionResult> Invoice(Guid id, Guid invoiceId, CancellationToken ct) => Respond(await platform.GetInvoiceAsync(id, invoiceId, ct));

    /// <summary>Manual verification: amount and currency must equal the invoice, the evidence reference must be new. Activates or renews the subscription and lifts a non-payment suspension.</summary>
    [HttpPost("tenants/{id:guid}/invoices/{invoiceId:guid}/verify"), Authorize(Policy = PermissionCodes.PlatformBillingVerify)]
    public async Task<IActionResult> Verify(Guid id, Guid invoiceId, VerifyBillingPaymentRequest request, CancellationToken ct) => Respond(await platform.VerifyPaymentAsync(id, invoiceId, request, ct));

    public sealed record VoidBody(string Reason);
    [HttpPost("tenants/{id:guid}/invoices/{invoiceId:guid}/void"), Authorize(Policy = PermissionCodes.PlatformBillingVerify)]
    public async Task<IActionResult> Void(Guid id, Guid invoiceId, VoidBody body, CancellationToken ct) => Respond(await platform.VoidInvoiceAsync(id, invoiceId, body.Reason, ct));

    // ---- settings and audit
    [HttpGet("settings"), Authorize(Policy = PermissionCodes.PlatformSettingsManage)]
    public async Task<PlatformSettingsDto> Settings(CancellationToken ct) => await settings.GetAllAsync(ct);

    [HttpPut("settings"), Authorize(Policy = PermissionCodes.PlatformSettingsManage)]
    public async Task<IActionResult> SaveSettings(PlatformSettingsDto dto, CancellationToken ct) => Respond(await settings.SaveAsync(dto, ct));

    [HttpGet("audit"), Authorize(Policy = PermissionCodes.PlatformAuditView)]
    public async Task<IActionResult> Audit([FromQuery] string? action, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) => Ok(await platform.AuditAsync(action, page, pageSize, ct));
}
