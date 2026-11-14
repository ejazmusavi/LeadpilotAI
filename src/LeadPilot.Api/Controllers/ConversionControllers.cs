using LeadPilot.Application.Common;
using LeadPilot.Application.Conversion;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Conversion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

/// <summary>Demo calendar. A demo can only be booked at an available slot returned by <c>GET slots</c>; times are UTC (the workspace time zone is in the settings). Ids of other workspaces answer 404.</summary>
[Route("api/v1/demos")]
public class DemosController(IDemoService demos) : ApiControllerBase
{
    /// <summary>Free slots between two instants (max 35 days). A slot is bookable while at least one representative is free.</summary>
    [HttpGet("slots"), Authorize(Policy = PermissionCodes.DemosView)]
    public async Task<IReadOnlyList<SlotDto>> Slots([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] Guid? repUserId, CancellationToken ct) => await demos.SlotsAsync(from, to, repUserId, ct);

    [HttpGet, Authorize(Policy = PermissionCodes.DemosView)]
    public async Task<PagedResult<DemoDto>> List([FromQuery] DemoQuery query, CancellationToken ct) => await demos.ListAsync(query, ct);

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.DemosView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await demos.GetAsync(id, ct));

    [HttpPost, Authorize(Policy = PermissionCodes.DemosBook)]
    public async Task<IActionResult> Book(BookDemoRequest request, CancellationToken ct)
    { var r = await demos.BookAsync(request, DemoSource.Staff, ct); return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r); }

    [HttpPost("{id:guid}/reschedule"), Authorize(Policy = PermissionCodes.DemosBook)]
    public async Task<IActionResult> Reschedule(Guid id, RescheduleDemoRequest request, CancellationToken ct) => Respond(await demos.RescheduleAsync(id, request, ct));

    [HttpPost("{id:guid}/cancel"), Authorize(Policy = PermissionCodes.DemosBook)]
    public async Task<IActionResult> Cancel(Guid id, CancelDemoRequest request, CancellationToken ct) => Respond(await demos.CancelAsync(id, request, ct));

    [HttpPost("{id:guid}/accept"), Authorize(Policy = PermissionCodes.DemosBook)]
    public async Task<IActionResult> Accept(Guid id, CancellationToken ct) => Respond(await demos.AcceptAsync(id, ct));

    [HttpPost("{id:guid}/confirm"), Authorize(Policy = PermissionCodes.DemosBook)]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken ct) => Respond(await demos.ConfirmAsync(id, ct));

    /// <summary>Only after the demo has started.</summary>
    [HttpPost("{id:guid}/complete"), Authorize(Policy = PermissionCodes.DemosBook)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct) => Respond(await demos.CompleteAsync(id, ct));

    [HttpPost("{id:guid}/no-show"), Authorize(Policy = PermissionCodes.DemosBook)]
    public async Task<IActionResult> NoShow(Guid id, CancellationToken ct) => Respond(await demos.NoShowAsync(id, ct));

    [HttpPut("{id:guid}"), Authorize(Policy = PermissionCodes.DemosBook)]
    public async Task<IActionResult> Update(Guid id, UpdateDemoRequest request, CancellationToken ct) => Respond(await demos.UpdateAsync(id, request, ct));
}

/// <summary>Demo, trial and payment-request settings, sales representatives, weekly hours and exceptions.</summary>
[Route("api/v1/demo-settings")]
public class DemoSettingsController(IConversionSettingsService settings) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.DemosView)]
    public async Task<ConversionSettingsDto> Get(CancellationToken ct) => await settings.GetAsync(ct);

    [HttpPut, Authorize(Policy = PermissionCodes.DemosManage)]
    public async Task<IActionResult> Save(SaveConversionSettingsRequest request, CancellationToken ct) => Respond(await settings.SaveAsync(request, ct));

    [HttpGet("reps"), Authorize(Policy = PermissionCodes.DemosView)]
    public async Task<IReadOnlyList<DemoRepDto>> Reps(CancellationToken ct) => await settings.RepsAsync(ct);

    [HttpPut("reps"), Authorize(Policy = PermissionCodes.DemosManage)]
    public async Task<IActionResult> SaveRep(SaveRepRequest request, CancellationToken ct) => Respond(await settings.SaveRepAsync(request, ct));

    [HttpPut("reps/{id:guid}/hours"), Authorize(Policy = PermissionCodes.DemosManage)]
    public async Task<IActionResult> SaveHours(Guid id, SaveRulesRequest request, CancellationToken ct) => Respond(await settings.SaveRulesAsync(id, request, ct));

    [HttpPost("reps/{id:guid}/exceptions"), Authorize(Policy = PermissionCodes.DemosManage)]
    public async Task<IActionResult> AddException(Guid id, AddExceptionRequest request, CancellationToken ct) => Respond(await settings.AddExceptionAsync(id, request, ct));

    [HttpDelete("reps/{id:guid}/exceptions/{exceptionId:guid}"), Authorize(Policy = PermissionCodes.DemosManage)]
    public async Task<IActionResult> RemoveException(Guid id, Guid exceptionId, CancellationToken ct) => Respond(await settings.RemoveExceptionAsync(id, exceptionId, ct));
}

/// <summary>Trials. A trial is recorded here; the account in the product is created by staff (or an installed provisioning backend) and the trial only starts when it is marked provisioned.</summary>
[Route("api/v1/trials")]
public class TrialsController(ITrialService trials) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.TrialsView)]
    public async Task<PagedResult<TrialDto>> List([FromQuery] TrialQuery query, CancellationToken ct) => await trials.ListAsync(query, ct);

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.TrialsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await trials.GetAsync(id, ct));

    [HttpPost, Authorize(Policy = PermissionCodes.TrialsManage)]
    public async Task<IActionResult> Create(CreateTrialRequest request, CancellationToken ct)
    { var r = await trials.CreateAsync(request, TrialSource.Staff, ct); return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r); }

    [HttpPost("{id:guid}/provisioned"), Authorize(Policy = PermissionCodes.TrialsManage)]
    public async Task<IActionResult> Provisioned(Guid id, ProvisionTrialRequest request, CancellationToken ct) => Respond(await trials.MarkProvisionedAsync(id, request, ct));

    [HttpPost("{id:guid}/extend"), Authorize(Policy = PermissionCodes.TrialsManage)]
    public async Task<IActionResult> Extend(Guid id, ExtendTrialRequest request, CancellationToken ct) => Respond(await trials.ExtendAsync(id, request, ct));

    [HttpPost("{id:guid}/cancel"), Authorize(Policy = PermissionCodes.TrialsManage)]
    public async Task<IActionResult> Cancel(Guid id, CancelTrialRequest request, CancellationToken ct) => Respond(await trials.CancelAsync(id, request, ct));

    /// <summary>Records usage figures that were observed (manual update, or the product's own integration). Nothing is estimated.</summary>
    [HttpPut("{id:guid}/usage"), Authorize(Policy = PermissionCodes.TrialsManage)]
    public async Task<IActionResult> Usage(Guid id, RecordTrialUsageRequest request, CancellationToken ct) => Respond(await trials.RecordUsageAsync(id, request, ct));
}

/// <summary>Customers, created from Won opportunities. Conversion records the agreement and a PENDING subscription; only a verified payment activates it.</summary>
[Route("api/v1/customers")]
public class CustomersController(ICustomerService customers) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.CustomersView)]
    public async Task<PagedResult<CustomerListItemDto>> List([FromQuery] CustomerQuery query, CancellationToken ct) => await customers.ListAsync(query, ct);

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.CustomersView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await customers.GetAsync(id, ct));

    /// <summary>Won opportunities that have not been converted yet.</summary>
    [HttpGet("won-opportunities"), Authorize(Policy = PermissionCodes.CustomersView)]
    public async Task<IReadOnlyList<WonOpportunityDto>> Won(CancellationToken ct) => await customers.WonOpportunitiesAsync(ct);

    [HttpPost("convert"), Authorize(Policy = PermissionCodes.CustomersManage)]
    public async Task<IActionResult> Convert(ConvertOpportunityRequest request, CancellationToken ct)
    { var r = await customers.ConvertAsync(request, ct); return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r); }

    [HttpPut("{id:guid}/account"), Authorize(Policy = PermissionCodes.CustomersManage)]
    public async Task<IActionResult> SetAccount(Guid id, SetAccountRequest request, CancellationToken ct) => Respond(await customers.SetAccountAsync(id, request, ct));

    [HttpPost("{id:guid}/subscriptions/{subscriptionId:guid}/cancel"), Authorize(Policy = PermissionCodes.CustomersManage)]
    public async Task<IActionResult> CancelSubscription(Guid id, Guid subscriptionId, CancelSubscriptionRequest request, CancellationToken ct) => Respond(await customers.CancelSubscriptionAsync(id, subscriptionId, request, ct));
}

/// <summary>Payment requests and payment verification. Nothing a client, the AI or a workflow says can mark a payment as received: only evidence recorded by a person with Payments.Verify, or a signature-verified provider event.</summary>
[Route("api/v1/payments")]
public class PaymentsController(IPaymentService payments, IPaymentProviderService providers, IPaymentWebhookService webhooks) : ApiControllerBase
{
    [HttpGet("requests"), Authorize(Policy = PermissionCodes.CustomersView)]
    public async Task<PagedResult<PaymentRequestDto>> List([FromQuery] PaymentRequestQuery query, CancellationToken ct) => await payments.ListAsync(query, ct);

    [HttpGet("requests/{id:guid}"), Authorize(Policy = PermissionCodes.CustomersView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await payments.GetAsync(id, ct));

    [HttpPost("requests"), Authorize(Policy = PermissionCodes.PaymentsRequest)]
    public async Task<IActionResult> Create(CreatePaymentRequestRequest request, CancellationToken ct)
    { var r = await payments.CreateAsync(request, ct); return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r); }

    [HttpPost("requests/{id:guid}/cancel"), Authorize(Policy = PermissionCodes.PaymentsRequest)]
    public async Task<IActionResult> Cancel(Guid id, CancelPaymentRequestRequest request, CancellationToken ct) => Respond(await payments.CancelAsync(id, request, ct));

    /// <summary>Records that the money arrived, with evidence (receipt / transfer id). Amount and currency must equal the request. Activates the subscription.</summary>
    [HttpPost("requests/{id:guid}/verify"), Authorize(Policy = PermissionCodes.PaymentsVerify)]
    public async Task<IActionResult> Verify(Guid id, VerifyPaymentRequest request, CancellationToken ct) => Respond(await payments.VerifyManuallyAsync(id, request, ct));

    [HttpGet("providers"), Authorize(Policy = PermissionCodes.PaymentsVerify)]
    public async Task<IReadOnlyList<ProviderConfigDto>> Providers(CancellationToken ct) => await providers.ListAsync(ct);

    [HttpPut("providers"), Authorize(Policy = PermissionCodes.PaymentsVerify)]
    public async Task<IActionResult> SaveProvider(SaveProviderConfigRequest request, CancellationToken ct) => Respond(await providers.SaveAsync(request, ct));

    /// <summary>Provider-neutral webhook. Anonymous by design: the tenant comes from the webhook id and the SIGNATURE is verified before anything is read. 401 invalid signature, 404 unknown/inactive, 413 too large, 200 for applied/duplicate/ignored.</summary>
    [HttpPost("webhooks/{webhookId:guid}"), AllowAnonymous]
    public async Task<IActionResult> Webhook(Guid webhookId, CancellationToken ct)
    {
        if (Request.ContentLength is > 262144) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var r = await webhooks.ReceiveAsync(webhookId, headers, ms.ToArray(), ct);
        return r.Outcome switch
        {
            WebhookOutcome.InvalidSignature => Unauthorized(),
            WebhookOutcome.NotFound => NotFound(),
            WebhookOutcome.TooLarge => StatusCode(StatusCodes.Status413PayloadTooLarge),
            WebhookOutcome.Malformed => BadRequest(),
            _ => Ok(new { outcome = r.Outcome.ToString() }),
        };
    }
}
