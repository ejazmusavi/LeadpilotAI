using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeadPilot.Application.Common;
using LeadPilot.Application.Conversion;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>TEST-ONLY payment provider adapter ("fakepay"): HMAC-SHA256 hex of the body in X-Fake-Signature. The product ships no adapter; this exists so the webhook design can be tested against a local fake.</summary>
public sealed class FakeProviderAdapter : IPaymentProviderAdapter
{
    public string ProviderKey => "fakepay";
    public string DisplayName => "Fake provider (tests only)";
    public static string Sign(string secret, byte[] body) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    public bool VerifySignature(IReadOnlyDictionary<string, string> headers, byte[] body, string secret) =>
        headers.TryGetValue("X-Fake-Signature", out var sig) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sig), Encoding.UTF8.GetBytes(Sign(secret, body)));

    public ParsedProviderEvent? Parse(byte[] body)
    {
        try
        {
            using var d = JsonDocument.Parse(body); var r = d.RootElement;
            var kind = r.GetProperty("kind").GetString() == "succeeded" ? ProviderEventKind.PaymentSucceeded : ProviderEventKind.Other;
            return new ParsedProviderEvent(r.GetProperty("id").GetString()!, r.GetProperty("reference").GetString()!, r.GetProperty("amount").GetDecimal(), r.GetProperty("currency").GetString()!, kind, r.GetProperty("at").GetDateTime().ToUniversalTime(), r.GetProperty("paymentId").GetString()!);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
}

/// <summary>A tenant (created through the real API) with the AI/catalog setup of <see cref="AgentWorld"/> (plan "Standard", 500 USD), one demo rep that works every day, and the REAL Phase 10 services behind the AI tools.</summary>
public sealed class ConvWorld : IAsyncDisposable
{
    public AgentWorld W { get; private init; } = default!;
    public AppFixture App => W.App;
    public ApiSession Admin => W.Admin;
    public Guid TenantId => W.TenantId;
    public Guid AdminUserId => W.Ai.AdminUser.Id;
    public WorkerHarness Worker => W.Ai.Worker;
    public DateTime Now => DateTime.UtcNow + App.Clock.Offset;

    public static async Task<ConvWorld> OpenAsync(AppFixture app, bool rep = true, string zone = "UTC")
    {
        var w = await AgentWorld.OpenAsync(app);
        app.Ai.Phase10.UseReal = true;
        var cw = new ConvWorld { W = w };
        await cw.SettingsAsync(new() { ["timeZoneId"] = zone });
        if (rep) await cw.AddRepAsync(cw.AdminUserId, zone);
        return cw;
    }

    public async Task<ConversionSettingsDto> SettingsAsync(Dictionary<string, object?>? over = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["timeZoneId"] = "UTC", ["slotMinutes"] = 60, ["minNoticeMinutes"] = 0, ["maxDaysAhead"] = 60, ["assignment"] = "RoundRobin", ["meetingLinkTemplate"] = null, ["confirmAutomatedBookings"] = false,
            ["remindersEnabled"] = false, ["reminderChannelAccountId"] = null, ["reminderOffsets"] = "1440,60", ["reminderBody"] = null, ["trialDefaultDays"] = 14, ["trialExpiryNoticeHours"] = 48,
            ["trialCustomerNoticesEnabled"] = false, ["trialNoticeBody"] = null, ["paymentDueDays"] = 7, ["paymentInstructions"] = "Bank: Test Bank, IBAN TEST123. Quote the reference.",
        };
        if (over is not null) foreach (var (k, v) in over) body[k] = v;
        return await Crm.Ok<ConversionSettingsDto>(await Admin.Put("/api/v1/demo-settings", body));
    }

    public async Task<DemoRepDto> AddRepAsync(Guid userId, string zone = "UTC", int buffer = 0, int maxPerDay = 24, bool hours = true)
    {
        var rep = await Crm.Ok<DemoRepDto>(await Admin.Put("/api/v1/demo-settings/reps", new { userId, isActive = true, timeZoneId = zone, bufferMinutes = buffer, maxPerDay }));
        if (hours) rep = await Crm.Ok<DemoRepDto>(await Admin.Put($"/api/v1/demo-settings/reps/{rep.Id}/hours", new { rules = Enum.GetValues<DayOfWeek>().Select(d => new { day = d.ToString(), startMinute = 0, endMinute = 1440 }) }));
        return rep;
    }

    public async Task<List<SlotDto>> SlotsAsync(DateTime from, DateTime to, ApiSession? s = null) =>
        await Crm.Ok<List<SlotDto>>(await (s ?? Admin).Get($"/api/v1/demos/slots?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}"));

    /// <summary>The first free slot at least <paramref name="daysAhead"/> days away.</summary>
    public async Task<SlotDto> SlotAsync(int daysAhead = 1, int skip = 0)
    {
        var from = Now.Date.AddDays(daysAhead);
        return (await SlotsAsync(from, from.AddDays(1))).OrderBy(s => s.StartUtc).Skip(skip).First();
    }

    public Task<HttpResponseMessage> BookRawAsync(Guid leadId, string slotId, ApiSession? s = null) => (s ?? Admin).Post("/api/v1/demos", new { leadId, slotId });
    public async Task<DemoDto> BookAsync(Guid leadId, string slotId) => await Crm.Ok<DemoDto>(await BookRawAsync(leadId, slotId));
    public async Task<Guid> LeadAsync() => (await Crm.CreateLead(Admin)).Id;

    public Task<T> Db<T>(Func<LeadPilot.Infrastructure.Persistence.AppDbContext, Task<T>> work) => App.Db(work);

    public Task Scoped(Func<IServiceProvider, Task> work) => Worker.Scopes.RunAsync(TenantId, (sp, _) => work(sp));

    public async Task<(Guid LeadId, Guid OpportunityId)> WonOpportunityAsync(Guid? leadId = null, decimal value = 500m)
    {
        var lead = leadId ?? await LeadAsync();
        var opp = await Crm.Ok<LeadPilot.Application.Crm.OpportunityDto>(await Admin.Post($"/api/v1/leads/{lead}/opportunities", new { name = "School ERP deal", value, currency = "USD" }));
        var pipelines = await Crm.Ok<List<LeadPilot.Application.Crm.PipelineDto>>(await Admin.Get("/api/v1/pipelines"));
        var won = pipelines.SelectMany(p => p.Stages).First(s => s.Kind == LeadPilot.Domain.Crm.StageKind.Won);
        await Crm.Ok<LeadPilot.Application.Crm.OpportunityDto>(await Admin.Put($"/api/v1/opportunities/{opp.Id}/stage", new { stageId = won.Id }));
        return (lead, opp.Id);
    }

    public Task<HttpResponseMessage> ConvertRawAsync(Guid opportunityId, object? extra = null, ApiSession? s = null) =>
        (s ?? Admin).Post("/api/v1/customers/convert", new { opportunityId, planId = W.PlanId, terms = "12 months, billed monthly, 30 day cancellation.", billingCycle = "Monthly" });

    public async Task<CustomerDto> NewCustomerAsync(Guid? leadId = null)
    {
        var (_, opp) = await WonOpportunityAsync(leadId);
        return await Crm.Ok<CustomerDto>(await ConvertRawAsync(opp));
    }

    public async Task<(CustomerDto Customer, PaymentRequestDto Request)> NewRequestAsync(Guid? leadId = null)
    {
        var c = await NewCustomerAsync(leadId);
        var r = await Crm.Ok<PaymentRequestDto>(await Admin.Post("/api/v1/payments/requests", new { subscriptionId = c.Subscriptions.Single().Id }));
        return (c, r);
    }

    public Task<HttpResponseMessage> VerifyRawAsync(Guid requestId, decimal amount, string evidence, string currency = "USD", ApiSession? s = null) =>
        (s ?? Admin).Post($"/api/v1/payments/requests/{requestId}/verify", new { amount, currency, method = "BankTransfer", evidenceReference = evidence });

    public async Task<CustomerDto> CustomerAsync(Guid id) => await Crm.Ok<CustomerDto>(await Admin.Get($"/api/v1/customers/{id}"));

    public Task<List<LeadPilot.Domain.Messaging.OutboxMessage>> OutboxAsync(string type) =>
        App.Db(db => db.OutboxMessages.IgnoreQueryFilters().Where(o => o.TenantId == TenantId && o.Type == type).OrderBy(o => o.CreatedAtUtc).ToListAsync());

    public async Task ReminderSweepAsync() => await Scoped(sp => sp.GetRequiredService<IDemoReminderService>().ProcessDueAsync());
    public async Task TrialSweepAsync() => await Scoped(sp => sp.GetRequiredService<ITrialLifecycleService>().SweepAsync());

    public ValueTask DisposeAsync()
    {
        App.Clock.Offset = TimeSpan.Zero;
        App.Ai.Reset();
        return W.DisposeAsync();
    }
}
