using System.Net;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Common;
using LeadPilot.Domain.Billing;
using LeadPilot.Infrastructure.Billing;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>A fresh workspace plus the platform owner's API session. Plans, grants, overrides and verification all go through the real platform API; only the clock and direct period changes are test shortcuts.</summary>
public sealed class BillingWorld : IAsyncDisposable
{
    public AppFixture App { get; private init; } = default!;
    public ApiSession Owner { get; private init; } = default!;
    public ApiSession Admin { get; private init; } = default!;
    public TestUser AdminUser { get; private init; } = default!;
    public TestUser ReadOnlyUser { get; private init; } = default!;
    public Guid TenantId { get; private init; }
    private readonly TimeSpan _clockAtOpen;
    private BillingWorld(TimeSpan clock) => _clockAtOpen = clock;

    public IServiceProvider Services => App.World.Services;
    public DateTime Now => DateTime.UtcNow + App.Clock.Offset;

    public static async Task<BillingWorld> OpenAsync(AppFixture app)
    {
        var (tenant, admin, ro) = await app.World.NewTenantAsync("bl");
        var owner = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var adm = await ApiSession.LoginAsync(app, admin.Email, admin.Password);
        return new BillingWorld(app.Clock.Offset) { App = app, Owner = owner, Admin = adm, AdminUser = admin, ReadOnlyUser = ro, TenantId = tenant.Id };
    }

    public static string Code() => "t-" + Guid.NewGuid().ToString("N")[..10];

    public async Task<PlatformPlanDto> PlanAsync(decimal price = 0m, string currency = "USD", string cycle = "Monthly", Dictionary<string, long?>? limits = null, string[]? features = null, string visibility = "Public",
        bool trial = false, int? trialDays = null, string? name = null)
    {
        var res = await Owner.Post("/api/v1/platform/plans", new
        {
            code = Code(), name = name ?? "Plan " + Guid.NewGuid().ToString("N")[..6], description = "test plan", cycle, price, currency, isTrial = trial, trialDays, visibility, sortOrder = 5,
            limits = limits ?? new Dictionary<string, long?>(), features = features ?? Features.All.Select(f => f.Key).ToArray(),
        });
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"create plan failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await ApiSession.Json<PlatformPlanDto>(res);
    }

    public async Task<PlatformTenantDetail> GrantAsync(PlatformPlanDto plan, DateTime? until = null, Guid? tenantId = null)
    {
        var res = await Owner.Post($"/api/v1/platform/tenants/{tenantId ?? TenantId}/grant", new { planId = plan.Id, periodEndUtc = until, reason = "integration test" });
        Assert.True(res.IsSuccessStatusCode, $"grant failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await ApiSession.Json<PlatformTenantDetail>(res);
    }

    public async Task<BillingOverview> OverviewAsync(ApiSession? s = null) => await ApiSession.Json<BillingOverview>(await (s ?? Admin).Get("/api/v1/billing"));
    public async Task<UsageLine> UsageAsync(UsageDimension d, ApiSession? s = null) => (await OverviewAsync(s)).Entitlements.Usage.Single(u => u.Dimension == d);

    /// <summary>Runs the renewal / grace / suspension step for the tenant exactly as the Hangfire job does.</summary>
    public async Task<LifecycleOutcome> LifecycleAsync(Guid? tenantId = null)
    {
        var outcome = LifecycleOutcome.NothingToDo;
        await Services.GetRequiredService<ITenantScopeRunner>().RunAsync(tenantId ?? TenantId, async (sp, ct) => outcome = await sp.GetRequiredService<IBillingLifecycleService>().ProcessAsync(ct));
        return outcome;
    }

    public async Task RunJobAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BillingJob>().RunAsync(CancellationToken.None);
    }

    public async Task<List<InvoiceDto>> InvoicesAsync(ApiSession? s = null) => (await ApiSession.Json<PagedResult<InvoiceDto>>(await (s ?? Admin).Get("/api/v1/billing/invoices?pageSize=100"))).Items.ToList();

    public Task<HttpResponseMessage> VerifyRawAsync(InvoiceDto inv, decimal? amount = null, string? currency = null, string? evidence = null, Guid? tenantId = null) =>
        Owner.Post($"/api/v1/platform/tenants/{tenantId ?? TenantId}/invoices/{inv.Id}/verify", new { amount = amount ?? inv.Total, currency = currency ?? inv.Currency, method = "BankTransfer", evidenceReference = evidence ?? "TRX-" + Guid.NewGuid().ToString("N")[..10], paidAtUtc = (DateTime?)null, notes = "test" });

    public async Task<InvoiceDto> VerifyAsync(InvoiceDto inv, string? evidence = null)
    {
        var res = await VerifyRawAsync(inv, evidence: evidence);
        Assert.True(res.IsSuccessStatusCode, $"verify failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await ApiSession.Json<InvoiceDto>(res);
    }

    public async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> work) => await App.World.WithDb(work);

    public async Task<TenantSubscriptionDto> SubscriptionAsync() => (await OverviewAsync()).Entitlements.Subscription!;

    public async ValueTask DisposeAsync() { App.Clock.Offset = _clockAtOpen; await Task.CompletedTask; }
}

public sealed record ErrorBody(string? Code, string? Detail, int? Status);

public static class BillingAssert
{
    public static async Task<ErrorBody> ErrorAsync(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {(int)status} got {(int)r.StatusCode}: {text}");
        var e = System.Text.Json.JsonSerializer.Deserialize<ErrorBody>(text, ApiSession.JsonOptions)!;
        Assert.Equal(code, e.Code);
        return e;
    }
}
