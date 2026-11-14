using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Conversion;
using LeadPilot.Domain.Conversion;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Conversion;

/// <summary>Per-workspace configuration of a payment provider webhook (secret stored encrypted, never shown again). Only providers with an INSTALLED adapter can be configured: the product ships none.</summary>
public sealed class PaymentProviderService(CrmContext crm, IEnumerable<IPaymentProviderAdapter> adapters, ISecretProtector protector, ILogger<PaymentProviderService> logger) : IPaymentProviderService
{
    public IReadOnlyList<(string Key, string Name)> InstalledAdapters() => adapters.Select(a => (a.ProviderKey, a.DisplayName)).OrderBy(a => a.ProviderKey).ToList();

    public async Task<IReadOnlyList<ProviderConfigDto>> ListAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var rows = await crm.Db.PaymentProviderConfigs.AsNoTracking().Where(c => c.TenantId == tenantId).OrderBy(c => c.ProviderKey).ToListAsync(ct);
        return rows.Select(c => new ProviderConfigDto(c.Id, c.ProviderKey, adapters.FirstOrDefault(a => a.ProviderKey == c.ProviderKey)?.DisplayName ?? c.ProviderKey + " (adapter not installed)", c.WebhookId, c.IsActive, c.SecretProtected.Length > 0)).ToList();
    }

    public Task<Result<ProviderConfigDto>> SaveAsync(SaveProviderConfigRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var adapter = adapters.FirstOrDefault(a => string.Equals(a.ProviderKey, r.ProviderKey, StringComparison.Ordinal));
            if (adapter is null) return Result.Failure<ProviderConfigDto>(Error.Validation("No adapter is installed for that provider. The product ships none: one has to be added before a provider can be connected."));
            if (r.Secret is null || r.Secret.Trim().Length < 16) return Result.Failure<ProviderConfigDto>(Error.Validation("The webhook secret must have at least 16 characters."));
            var cfg = await db.PaymentProviderConfigs.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ProviderKey == adapter.ProviderKey, ct);
            if (cfg is null) { cfg = PaymentProviderConfig.Create(tenantId, adapter.ProviderKey, protector.Protect(r.Secret.Trim()), crm.Now, crm.Actor); db.PaymentProviderConfigs.Add(cfg); }
            else cfg.SetSecret(protector.Protect(r.Secret.Trim()), crm.Now);
            cfg.SetActive(r.IsActive, crm.Now);
            crm.Record("PaymentProvider.Saved", "PaymentProviderConfig", cfg.Id, $"Webhook configuration for provider '{adapter.ProviderKey}' saved (active: {r.IsActive}). The secret is stored encrypted.");
            await db.SaveChangesAsync(ct);
            return Result.Success(new ProviderConfigDto(cfg.Id, cfg.ProviderKey, adapter.DisplayName, cfg.WebhookId, cfg.IsActive, true));
        }, logger);
}

/// <summary>Cross-tenant lookup that must run BEFORE a tenant is known (the anonymous webhook): returns the tenant of a webhook id. Nothing else.</summary>
internal sealed class PaymentWebhookDirectory(AppDbContext db)
{
    public async Task<(Guid TenantId, string ProviderKey, string Secret, bool Active)?> ResolveAsync(Guid webhookId, ISecretProtector protector, CancellationToken ct)
    {
        var c = await db.PaymentProviderConfigs.IgnoreQueryFilters().AsNoTracking().Where(x => x.WebhookId == webhookId).Select(x => new { x.TenantId, x.ProviderKey, x.SecretProtected, x.IsActive }).FirstOrDefaultAsync(ct);
        if (c is null) return null;
        string secret;
        try { secret = protector.Unprotect(c.SecretProtected); } catch (Exception) { return null; }
        return (c.TenantId, c.ProviderKey, secret, c.IsActive);
    }
}

/// <summary>
/// Anonymous webhook core. Order is the security design: (1) size limit, (2) resolve tenant + provider from the random webhook id (unknown / inactive / no adapter = 404, no information), (3) verify the signature
/// with the tenant's secret BEFORE reading a single field of the body, (4) parse, (5) inside the tenant scope record the delivery under (provider, event id) - a redelivery stops here as Duplicate - and apply it
/// only if it is a success event that matches an open payment request of THAT tenant by reference, amount and currency. Everything else is recorded as Rejected/Ignored and changes nothing.
/// </summary>
public sealed class PaymentWebhookService(IEnumerable<IPaymentProviderAdapter> adapters, IServiceProvider services, ISecretProtector protector, IOptions<ConversionOptions> options, ILogger<PaymentWebhookService> logger) : IPaymentWebhookService
{
    public async Task<WebhookResult> ReceiveAsync(Guid webhookId, IReadOnlyDictionary<string, string> headers, byte[] body, CancellationToken ct = default)
    {
        if (body.Length > options.Value.MaxWebhookBytes) return new(WebhookOutcome.TooLarge);
        using var scope = services.CreateScope();
        var resolved = await scope.ServiceProvider.GetRequiredService<PaymentWebhookDirectory>().ResolveAsync(webhookId, protector, ct);
        var adapter = resolved is null ? null : adapters.FirstOrDefault(a => a.ProviderKey == resolved.Value.ProviderKey);
        if (resolved is null || !resolved.Value.Active || adapter is null) return new(WebhookOutcome.NotFound);
        if (!adapter.VerifySignature(headers, body, resolved.Value.Secret))
        {
            logger.LogWarning("Payment webhook {WebhookId} rejected: invalid signature", webhookId);
            return new(WebhookOutcome.InvalidSignature);
        }
        var parsed = adapter.Parse(body);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.EventId) || string.IsNullOrWhiteSpace(parsed.Reference)) return new(WebhookOutcome.Malformed);

        WebhookResult result = new(WebhookOutcome.Ignored);
        await scope.ServiceProvider.GetRequiredService<ITenantScopeRunner>().RunAsync(resolved.Value.TenantId, async (sp, c) => result = await ApplyAsync(sp, adapter.ProviderKey, parsed, c), ct);
        return result;
    }

    private static async Task<WebhookResult> ApplyAsync(IServiceProvider sp, string providerKey, ParsedProviderEvent e, CancellationToken ct)
    {
        var crm = sp.GetRequiredService<CrmContext>(); var core = sp.GetRequiredService<PaymentVerificationCore>();
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        if (await db.PaymentEvents.AnyAsync(x => x.TenantId == tenantId && x.ProviderKey == providerKey && x.ExternalEventId == e.EventId, ct)) return new(WebhookOutcome.Duplicate);

        async Task<WebhookResult> RecordAsync(PaymentEventOutcome outcome, string reason, Guid? requestId)
        {
            db.PaymentEvents.Add(PaymentEvent.Create(tenantId, providerKey, e.EventId, requestId, outcome, reason, e.Kind.ToString(), now));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex)) { ConversionSupport.DetachAdded(db); return new(WebhookOutcome.Duplicate); }
            return new(outcome == PaymentEventOutcome.Rejected ? WebhookOutcome.Rejected : WebhookOutcome.Ignored, reason);
        }

        if (e.Kind != ProviderEventKind.PaymentSucceeded) return await RecordAsync(PaymentEventOutcome.Ignored, "Not a payment success event.", null);
        var request = await db.PaymentRequests.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Reference == e.Reference, ct);
        if (request is null) return await RecordAsync(PaymentEventOutcome.Rejected, "No payment request with that reference in this workspace.", null);
        if (request.Status != PaymentRequestStatus.Open) return await RecordAsync(PaymentEventOutcome.Rejected, $"The payment request is already {request.Status}.", request.Id);
        if (e.Amount != request.Amount || !string.Equals(e.Currency, request.Currency, StringComparison.OrdinalIgnoreCase)) return await RecordAsync(PaymentEventOutcome.Rejected, "Amount or currency does not match the payment request.", request.Id);

        var row = PaymentEvent.Create(tenantId, providerKey, e.EventId, request.Id, PaymentEventOutcome.Applied, null, e.Kind.ToString(), now);
        var applied = await core.ApplyAsync(request.Id, e.Amount, e.Currency, PaymentMethod.Provider, VerificationSource.ProviderWebhook, $"{providerKey}:{e.ProviderPaymentId}", null, providerKey, e.EventId, e.OccurredAtUtc > now ? now : e.OccurredAtUtc, null, row, ct);
        if (applied.IsSuccess) return new(WebhookOutcome.Applied);
        if (applied.Error.Code == "conflict") return await RecordAsync(PaymentEventOutcome.Rejected, applied.Error.Message, request.Id);
        return await RecordAsync(PaymentEventOutcome.Rejected, applied.Error.Message, request.Id);
    }
}
