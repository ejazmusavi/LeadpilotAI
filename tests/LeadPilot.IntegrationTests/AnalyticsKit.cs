using System.Globalization;
using System.Reflection;
using LeadPilot.Application.Analytics;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace LeadPilot.IntegrationTests;

/// <summary>
/// TEST-ONLY row factory: builds entities with exactly the columns an analytics query reads, bypassing the domain workflow (so every timestamp can be placed on a day boundary) and with foreign-key checks switched
/// off for the seeding connection only. The metrics under test read these raw rows; the expectations are computed with independent raw SQL and hand-counted constants.
/// </summary>
public static class Rows
{
    public static T Make<T>(Guid tenantId, params (string Name, object? Value)[] props) where T : class
    {
        var e = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
        void Set(string n, object? v)
        {
            var p = typeof(T).GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new InvalidOperationException($"{typeof(T).Name}.{n} does not exist");
            p.SetValue(e, v);
        }
        Set("TenantId", tenantId);
        foreach (var (n, v) in props) Set(n, v);
        return e;
    }
}

public sealed class AnalyticsWorld : IAsyncDisposable
{
    public AppFixture App { get; private init; } = default!;
    public Guid TenantId { get; private init; }
    public string Zone { get; private init; } = "UTC";
    public TimeZoneInfo Tz => TimeZoneInfo.FindSystemTimeZoneById(Zone);
    public TestUser Admin { get; private init; } = default!;
    public TestUser Manager { get; private init; } = default!;
    public TestUser Agent { get; private init; } = default!;
    public TestUser Agent2 { get; private init; } = default!;
    public TestUser ReadOnly { get; private init; } = default!;
    public TestUser Marketing { get; private init; } = default!;
    public ApiSession AdminApi { get; private init; } = default!;
    public ApiSession ManagerApi { get; private init; } = default!;
    public ApiSession AgentApi { get; private init; } = default!;
    public WorkerHarness Worker { get; private init; } = default!;
    public Guid PipelineId { get; private set; }
    public Guid OpenStageId { get; private set; }
    public Guid WonStageId { get; private set; }
    public Guid LostStageId { get; private set; }
    public Guid StatusId { get; private set; }

    public static async Task<AnalyticsWorld> OpenAsync(AppFixture app, string zone = "Asia/Karachi", string prefix = "an")
    {
        var (tenant, admin, ro) = await app.World.NewTenantAsync(prefix);
        var sfx = Guid.NewGuid().ToString("N")[..6];
        var mgr = await app.World.AddUserAsync(tenant.Id, $"{prefix}-mgr-{sfx}@c.test", SystemRoles.SalesManager);
        var agent = await app.World.AddUserAsync(tenant.Id, $"{prefix}-agent-{sfx}@c.test", SystemRoles.SalesAgent);
        var agent2 = await app.World.AddUserAsync(tenant.Id, $"{prefix}-agent2-{sfx}@c.test", SystemRoles.SalesAgent);
        var mkt = await app.World.AddUserAsync(tenant.Id, $"{prefix}-mkt-{sfx}@c.test", SystemRoles.MarketingManager);
        var w = new AnalyticsWorld
        {
            App = app, TenantId = tenant.Id, Zone = zone, Admin = admin, ReadOnly = ro, Manager = mgr, Agent = agent, Agent2 = agent2, Marketing = mkt,
            AdminApi = await ApiSession.LoginAsync(app, admin.Email, admin.Password), ManagerApi = await ApiSession.LoginAsync(app, mgr.Email, mgr.Password), AgentApi = await ApiSession.LoginAsync(app, agent.Email, agent.Password),
            Worker = MessagingKit.BuildWorker(app)
        };
        await w.InitAsync();
        return w;
    }

    private async Task InitAsync()
    {
        await App.World.WithDb(async db =>
        {
            var stages = await db.PipelineStages.IgnoreQueryFilters().Where(s => s.TenantId == TenantId).OrderBy(s => s.Position).ToListAsync();
            PipelineId = stages[0].PipelineId;
            OpenStageId = stages.First(s => s.Kind == StageKind.Open).Id; WonStageId = stages.First(s => s.Kind == StageKind.Won).Id; LostStageId = stages.First(s => s.Kind == StageKind.Lost).Id;
            StatusId = await db.LeadStatuses.IgnoreQueryFilters().Where(s => s.TenantId == TenantId && s.IsDefault).Select(s => s.Id).FirstAsync();
            var cs = ConversionSettings.Create(TenantId, DateTime.UtcNow);
            typeof(ConversionSettings).GetProperty("TimeZoneId")!.SetValue(cs, Zone);
            db.ConversionSettings.Add(cs);
            await db.SaveChangesAsync();
        });
    }

    // ---------- time helpers ----------

    public DateOnly Today => RangeResolver.LocalDay(Tz, DateTime.UtcNow + App.Clock.Offset);
    /// <summary>Workspace-local wall-clock time as a UTC instant.</summary>
    public DateTime L(DateOnly day, int h = 0, int m = 0, int s = 0) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(h, m, s)), DateTimeKind.Unspecified), Tz);

    // ---------- seeding ----------

    public async Task SeedAsync(Func<AppDbContext, Task> add)
    {
        await App.World.WithDb(async db =>
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS=0");
            try { await add(db); await db.SaveChangesAsync(); }
            finally { await db.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS=1"); await db.Database.CloseConnectionAsync(); }
        });
    }

    public T M<T>(params (string, object?)[] p) where T : class => Rows.Make<T>(TenantId, p);

    public Lead NewLead(string name, DateTime createdUtc, Guid? owner, string? city = "Lahore", LeadSourceType source = LeadSourceType.Manual, bool archived = false, Guid? stage = null) =>
        M<Lead>(("Name", name), ("NormalizedName", name.ToLowerInvariant()), ("DedupKey", name.ToLowerInvariant() + "|" + city), ("PipelineId", PipelineId), ("PipelineStageId", stage ?? OpenStageId), ("StatusId", StatusId), ("OwnerUserId", owner),
            ("City", city), ("PrimarySourceType", source), ("IsArchived", archived), ("ArchivedAtUtc", archived ? createdUtc.AddHours(1) : null), ("CreatedAtUtc", createdUtc), ("LastActivityAtUtc", createdUtc));

    private int _phone = 700_000;
    public string NextPhone() => "+9230" + Interlocked.Increment(ref _phone);

    public Contact NewContact(Lead lead, DataOrigin origin = DataOrigin.Public, bool deleted = false, DateTime? phoneVerified = null, string? phone = null)
    {
        var ph = phone ?? NextPhone();
        return M<Contact>(("LeadId", lead.Id), ("Name", "Contact"), ("PhoneRaw", ph), ("PhoneE164", ph), ("PhoneUniqueKey", deleted ? null : ph), ("DataOrigin", origin), ("PhoneVerifiedAtUtc", phoneVerified), ("IsDeleted", deleted), ("DeletedAtUtc", deleted ? DateTime.UtcNow : null), ("CreatedAtUtc", lead.CreatedAtUtc));
    }

    public ConsentRecord Consent(Contact c, ConsentStatus status, DateTime at, ConsentChannel channel = ConsentChannel.WhatsApp, ConsentPurpose purpose = ConsentPurpose.Marketing) =>
        M<ConsentRecord>(("ContactId", c.Id), ("Channel", channel), ("Purpose", purpose), ("Status", status), ("IsRevocation", status == ConsentStatus.Revoked), ("Source", "test"), ("OccurredAtUtc", at), ("RecordedAtUtc", at));

    public Conversation Conv(Contact? c) => M<Conversation>(("ChannelAccountId", Guid.NewGuid()), ("ExternalChatId", Guid.NewGuid().ToString("N")), ("ContactId", c?.Id), ("Status", ConversationStatus.Open), ("CreatedAtUtc", DateTime.UtcNow));

    public Message Out(Conversation cv, Contact? c, DateTime? sent, DateTime? delivered = null, DateTime? failed = null, Guid? by = null, bool ai = false, bool read = false) =>
        M<Message>(("ConversationId", cv.Id), ("ChannelAccountId", cv.ChannelAccountId), ("ContactId", c?.Id), ("Direction", MessageDirection.Outbound), ("Status", failed is not null ? MessageStatus.Failed : delivered is not null ? MessageStatus.Delivered : MessageStatus.Sent),
            ("Body", "hello"), ("CorrelationId", Guid.NewGuid().ToString("N")), ("CreatedByUserId", by), ("CreatedAtUtc", sent ?? failed ?? DateTime.UtcNow), ("OccurredAtUtc", sent ?? failed ?? DateTime.UtcNow),
            ("SentAtUtc", sent), ("DeliveredAtUtc", delivered), ("ReadAtUtc", read ? delivered : null), ("FailedAtUtc", failed), ("IsAiGenerated", ai));

    public Message In(Conversation cv, Contact? c, DateTime at) =>
        M<Message>(("ConversationId", cv.Id), ("ChannelAccountId", cv.ChannelAccountId), ("ContactId", c?.Id), ("Direction", MessageDirection.Inbound), ("Status", MessageStatus.Received), ("Body", "reply"), ("CorrelationId", Guid.NewGuid().ToString("N")), ("CreatedAtUtc", at), ("OccurredAtUtc", at));

    // ---------- raw SQL (independent of the LINQ under test) ----------

    public static string ConnectionString => Environment.GetEnvironmentVariable("ConnectionStrings__Default")!;

    public async Task<T> Sql<T>(string sql, params (string, object?)[] p)
    {
        await using var c = new MySqlConnection(ConnectionString); await c.OpenAsync();
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@t", TenantId.ToString());
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        var o = await cmd.ExecuteScalarAsync();
        return o is null or DBNull ? default! : (T)Convert.ChangeType(o, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    public async Task<List<object?[]>> SqlRows(string sql, params (string, object?)[] p)
    {
        await using var c = new MySqlConnection(ConnectionString); await c.OpenAsync();
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("@t", TenantId.ToString());
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        var list = new List<object?[]>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) { var row = new object?[r.FieldCount]; r.GetValues(row!); list.Add(row.Select(x => x is DBNull ? null : x).ToArray()); }
        return list;
    }

    // ---------- driving the code under test ----------

    public async Task<T> Scoped<T>(Func<IServiceProvider, Task<T>> work)
    {
        T result = default!;
        await Worker.Scopes.RunAsync(TenantId, async (sp, _) => { result = await work(sp); });
        return result;
    }
    public Task Scoped(Func<IServiceProvider, Task> work) => Worker.Scopes.RunAsync(TenantId, (sp, _) => work(sp));
    public Task<RollupResult> RefreshAsync(bool full = false) => Scoped(sp => sp.GetRequiredService<IAnalyticsRollupService>().RefreshAsync(full));

    public static string Qs(RangeRequest r) => $"preset={r.Preset}" + (r.From is { } f ? $"&from={f:yyyy-MM-dd}" : "") + (r.To is { } t ? $"&to={t:yyyy-MM-dd}" : "") + $"&compare={r.Compare.ToString().ToLowerInvariant()}";
    public static RangeRequest Custom(DateOnly from, DateOnly to, bool compare = true) => new() { Preset = RangePreset.Custom, From = from, To = to, Compare = compare };

    public async Task<DashboardDto> DashboardAsync(ApiSession s, RangeRequest r) => await Crm.Ok<DashboardDto>(await s.Get("/api/v1/analytics/dashboard?" + Qs(r)));
    public async Task<ReportDto> ReportAsync(ApiSession s, string key, RangeRequest r, int page = 1, int size = 50) => await Crm.Ok<ReportDto>(await s.Get($"/api/v1/analytics/reports/{key}?{Qs(r)}&page={page}&pageSize={size}"));

    public ValueTask DisposeAsync() => Worker.DisposeAsync();
}

public static class DashboardExtensions
{
    public static MetricValueDto Metric(this DashboardDto d, string key) => d.Metrics.Single(m => m.Key == key);
}
