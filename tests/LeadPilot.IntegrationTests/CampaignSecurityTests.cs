using System.Net;
using System.Net.Http.Json;
using LeadPilot.Infrastructure.Messaging;
using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Messaging.Broker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeadPilot.IntegrationTests;

/// <summary>Permissions (401/403/404) and tenant isolation of every campaign endpoint, job, consumer and database guard.</summary>
[Collection("app")]
public class CampaignSecurityTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static readonly Guid Unknown = Guid.NewGuid();

    private sealed record Endpoint(string Method, string Url, string Permission, bool NeedsId, object? Body = null);

    private static IEnumerable<Endpoint> Endpoints() =>
    [
        new("GET", "/api/v1/campaigns", PermissionCodes.CampaignsView, false), new("GET", "/api/v1/campaigns/{id}", PermissionCodes.CampaignsView, true),
        new("POST", "/api/v1/campaigns", PermissionCodes.CampaignsCreate, false, new { }), new("PUT", "/api/v1/campaigns/{id}", PermissionCodes.CampaignsUpdate, true, new { name = "x", type = "Manual" }),
        new("DELETE", "/api/v1/campaigns/{id}", PermissionCodes.CampaignsDelete, true), new("POST", "/api/v1/campaigns/{id}/clone", PermissionCodes.CampaignsCreate, true, new { }),
        new("POST", "/api/v1/campaigns/{id}/submit", PermissionCodes.CampaignsUpdate, true), new("POST", "/api/v1/campaigns/{id}/approve", PermissionCodes.CampaignsApprove, true),
        new("POST", "/api/v1/campaigns/{id}/reject", PermissionCodes.CampaignsApprove, true, new { reason = "no" }), new("POST", "/api/v1/campaigns/{id}/unschedule", PermissionCodes.CampaignsUpdate, true),
        new("POST", "/api/v1/campaigns/{id}/start", PermissionCodes.CampaignsExecute, true), new("POST", "/api/v1/campaigns/{id}/pause", PermissionCodes.CampaignsExecute, true),
        new("POST", "/api/v1/campaigns/{id}/resume", PermissionCodes.CampaignsExecute, true), new("POST", "/api/v1/campaigns/{id}/cancel", PermissionCodes.CampaignsCancel, true),
        new("POST", "/api/v1/campaigns/{id}/test-send", PermissionCodes.CampaignsExecute, true, new { phoneE164 = "+923001112233" }), new("POST", "/api/v1/campaigns/{id}/dry-run", PermissionCodes.CampaignsExecute, true),
        new("GET", "/api/v1/campaigns/{id}/recipients", PermissionCodes.CampaignsView, true), new("GET", "/api/v1/campaigns/{id}/log", PermissionCodes.CampaignsView, true),
        new("GET", "/api/v1/campaigns/{id}/conversions", PermissionCodes.CampaignsView, true), new("GET", "/api/v1/campaigns/{id}/stats", PermissionCodes.CampaignsView, true),
        new("POST", "/api/v1/campaigns/audience/preview", PermissionCodes.AudiencesPreview, false, new { type = "Manual", audience = new { } }),
        new("GET", "/api/v1/campaigns/settings", PermissionCodes.CampaignsView, false),
        new("PUT", "/api/v1/campaigns/settings", PermissionCodes.CampaignsSettings, false, new { }), new("PUT", "/api/v1/campaigns/settings/kill-switch", PermissionCodes.CampaignsSettings, false, new { enabled = false }),
    ];

    private static Task<HttpResponseMessage> Call(ApiSession s, Endpoint e, Guid id) =>
        s.Http.SendAsync(new HttpRequestMessage(new HttpMethod(e.Method), e.Url.Replace("{id}", id.ToString())) { Content = e.Body is null ? null : JsonContent.Create(e.Body, options: ApiSession.JsonOptions) });

    [RequiresMySqlFact]
    public async Task Every_campaign_endpoint_answers_401_to_anonymous_callers()
    {
        var anon = ApiSession.Anonymous(app);
        foreach (var e in Endpoints()) Assert.True(HttpStatusCode.Unauthorized == (await Call(anon, e, Unknown)).StatusCode, $"{e.Method} {e.Url}");
    }

    [RequiresMySqlFact]
    public async Task Every_campaign_endpoint_enforces_its_permission_for_every_default_role_and_unknown_ids_are_404()
    {
        var users = new (TestUser User, string Role)[] { (W.AReadOnly, SystemRoles.ReadOnly), (W.AMarketing, SystemRoles.MarketingManager), (W.ASalesManager, SystemRoles.SalesManager), (W.ASalesAgent, SystemRoles.SalesAgent), (W.AAdmin, SystemRoles.TenantAdministrator) };
        foreach (var (user, role) in users)
        {
            var session = await ApiSession.LoginAsync(app, user.Email, user.Password);
            var granted = SystemRoles.All.Single(r => r.Name == role).Permissions.ToHashSet();
            foreach (var e in Endpoints())
            {
                var res = await Call(session, e, Unknown);
                if (!granted.Contains(e.Permission)) Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"{role} must be refused {e.Method} {e.Url} but got {(int)res.StatusCode}");
                else
                {
                    Assert.True(res.StatusCode != HttpStatusCode.Forbidden && res.StatusCode != HttpStatusCode.Unauthorized, $"{role} must be allowed {e.Method} {e.Url} but got {(int)res.StatusCode}");
                    if (e.NeedsId) Assert.True(res.StatusCode == HttpStatusCode.NotFound, $"{role} {e.Method} {e.Url} with an unknown id must be 404 but got {(int)res.StatusCode}");
                }
            }
        }
    }

    [RequiresMySqlFact]
    public async Task The_default_roles_match_the_documented_split_of_duties()
    {
        var mk = SystemRoles.All.Single(r => r.Name == SystemRoles.MarketingManager).Permissions; var sm = SystemRoles.All.Single(r => r.Name == SystemRoles.SalesManager).Permissions;
        Assert.Contains(PermissionCodes.CampaignsExecute, mk); Assert.DoesNotContain(PermissionCodes.CampaignsApprove, mk);   // a marketer runs campaigns but approval is somebody else's job
        Assert.Contains(PermissionCodes.CampaignsApprove, sm); Assert.DoesNotContain(PermissionCodes.CampaignsCreate, sm);
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- tenant isolation

    [RequiresMySqlFact]
    public async Task Tenant_B_cannot_see_edit_run_pause_approve_or_preview_tenant_A_campaigns_or_recipients()
    {
        await using var a = await CampaignWorld.OpenAsync(app);
        var b = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);   // sign in before the test clock moves: tokens are issued with it
        var contacts = await a.ContactsAsync(2);
        var camp = await a.CreateAsync(contacts); await a.StartAsync(camp.Id); await a.RunToEndAsync(camp.Id);
        var sentBefore = app.Waha.SentMessages.Count;

        foreach (var e in Endpoints().Where(e => e.NeedsId))
        {
            var res = await Call(b, e, camp.Id);
            Assert.True(res.StatusCode == HttpStatusCode.NotFound, $"B {e.Method} {e.Url} must be 404 but got {(int)res.StatusCode}");
        }
        var list = await Crm.Page<CampaignListItemDto>(await b.Get("/api/v1/campaigns?pageSize=100"));
        Assert.DoesNotContain(list.Items, c => c.Id == camp.Id);
        Assert.Equal(CampaignStatus.Completed, (await a.GetAsync(camp.Id)).Status);                           // A's campaign untouched by all of the above
        Assert.Equal(2, (await a.RecipientsAsync(camp.Id)).Count);

        // references to A's records inside B's requests are not found / not usable
        var reqs = new object[]
        {
            new { name = "x", type = "Manual", templateId = a.TemplateId, audience = new { contactIds = contacts.Select(c => c.ContactId).ToArray() } },
            new { name = "x", type = "Manual", channelAccountId = a.Account.Id, audience = new { contactIds = contacts.Select(c => c.ContactId).ToArray() } },
            new { name = "x", type = "FollowUp", audience = new { followUp = new { parentCampaignId = camp.Id, rule = "SentNotReplied", afterDays = 0 } } },
        };
        foreach (var body in reqs) Assert.Equal(HttpStatusCode.BadRequest, (await b.Post("/api/v1/campaigns", body)).StatusCode);
        var prev = await Crm.Ok<AudiencePreviewDto>(await b.Post("/api/v1/campaigns/audience/preview", new { type = "Manual", audience = new { contactIds = contacts.Select(c => c.ContactId).ToArray() } }));
        Assert.Equal(0, prev.Candidates); Assert.Equal(0, prev.Eligible);                                       // A's contacts do not exist for B
        Assert.Equal(HttpStatusCode.NotFound, (await b.Post("/api/v1/campaigns/audience/preview", new { type = "FollowUp", audience = new { followUp = new { parentCampaignId = camp.Id, rule = "SentNotReplied", afterDays = 0 } } })).StatusCode);

        // A's workspace policy and kill switch are its own
        await Crm.Ok<CampaignSettingsDto>(await b.Put("/api/v1/campaigns/settings/kill-switch", new { enabled = true }));
        try { Assert.False((await Crm.Ok<CampaignSettingsDto>(await a.Admin.Get("/api/v1/campaigns/settings"))).KillSwitch); }
        finally { await Crm.Ok<CampaignSettingsDto>(await b.Put("/api/v1/campaigns/settings/kill-switch", new { enabled = false })); }
        Assert.Equal(sentBefore, app.Waha.SentMessages.Count);
    }

    [RequiresMySqlFact]
    public async Task A_spoofed_tenant_id_is_rejected_on_every_campaign_request_body_and_query()
    {
        await using var a = await CampaignWorld.OpenAsync(app);
        var c = await a.ContactAsync(); var camp = await a.CreateAsync([c]);
        var bTenant = W.B.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.Post("/api/v1/campaigns", new { name = "x", type = "Manual", audience = new { contactIds = new[] { c.ContactId } }, tenantId = bTenant })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.Put($"/api/v1/campaigns/{camp.Id}", new { name = "x", type = "Manual", audience = new { contactIds = new[] { c.ContactId } }, tenantId = bTenant })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.Post("/api/v1/campaigns/audience/preview", new { type = "Manual", audience = new { contactIds = new[] { c.ContactId } }, tenantId = bTenant })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.Put("/api/v1/campaigns/settings", new { cooldownDays = 1, tenantId = bTenant })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.Put("/api/v1/campaigns/settings/kill-switch", new { enabled = true, tenantId = bTenant })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.Post($"/api/v1/campaigns/{camp.Id}/test-send", new { phoneE164 = "+923001112233", tenantId = bTenant })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.Get($"/api/v1/campaigns?tenantId={bTenant}")).StatusCode);
        Assert.False((await Crm.Ok<CampaignSettingsDto>(await a.Admin.Get("/api/v1/campaigns/settings"))).KillSwitch);
    }

    [RequiresMySqlFact]
    public async Task A_job_for_one_tenant_never_runs_or_sends_through_another_tenants_scope_or_account()
    {
        await using var a = await CampaignWorld.OpenAsync(app);
        var contacts = await a.ContactsAsync(2); var camp = await a.CreateAsync(contacts); await a.StartAsync(camp.Id);
        await a.TickAsync(camp.Id);
        var queued = await a.QueuedMessagesAsync(camp.Id); Assert.Equal(2, queued.Count);
        var sent = app.Waha.SentMessages.Count; var baseline = sent;
        // the Hangfire arguments of A's campaign / message executed inside B's tenant scope: nothing is found, nothing happens
        await a.RunAsync(sp => sp.GetRequiredService<ICampaignRunner>().TickAsync(camp.Id, default), W.B.Id);
        await a.RunAsync(sp => sp.GetRequiredService<ICampaignRunner>().StartAsync(camp.Id, default), W.B.Id);
        await a.Worker.RunSendAsync(W.B.Id, queued[0]);
        Assert.Equal(sent, app.Waha.SentMessages.Count);
        Assert.Equal(2, (await a.QueuedMessagesAsync(camp.Id)).Count);                                           // still waiting for A's own worker
        // and a job for a tenant that does not exist fails closed
        await Assert.ThrowsAnyAsync<Exception>(() => a.RunAsync(sp => sp.GetRequiredService<ICampaignRunner>().TickAsync(camp.Id, default), Guid.NewGuid()));
        await Assert.ThrowsAnyAsync<Exception>(() => a.RunAsync(sp => sp.GetRequiredService<ICampaignRunner>().TickAsync(camp.Id, default), Guid.Empty));
        await a.SendQueuedAsync(camp.Id);
        Assert.All(contacts, c => Assert.Single(a.SentTo(c)));
        Assert.All(app.Waha.SentMessages.Skip(baseline), m => { Assert.Equal(a.Account.Session, m.Session); Assert.Contains(contacts, c => c.Chat == m.ChatId); });   // only A's recipients, only via A's session
    }

    [RequiresMySqlFact]
    public async Task A_campaign_cannot_be_pointed_at_another_tenants_account_so_a_job_can_never_send_through_it()
    {
        await using var a = await CampaignWorld.OpenAsync(app, freshTenant: true); await using var b = await CampaignWorld.OpenAsync(app, freshTenant: true);
        var c = await a.ContactAsync();
        var res = await CampaignWorld.CreateRawAsync(a.Admin, new { name = "x", type = "Manual", templateId = a.TemplateId, channelAccountId = b.Account.Id, audience = new { contactIds = new[] { c.ContactId } } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        // even by force, the database guard refuses the reference
        var camp = await a.CreateAsync([c]);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => app.Db(db => db.Campaigns.IgnoreQueryFilters().Where(x => x.Id == camp.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ChannelAccountId, b.Account.Id))).ContinueWith(_ => app.Db(async d =>
        {
            var entity = await d.Campaigns.IgnoreQueryFilters().FirstAsync(x => x.Id == camp.Id);
            d.Entry(entity).Property(x => x.ChannelAccountId).CurrentValue = b.Account.Id; d.Entry(entity).Property(x => x.ChannelAccountId).IsModified = true;
            await d.SaveChangesAsync();
        })).Unwrap());
        Assert.Contains("another tenant", ex.ToString());
    }

    [RequiresMySqlFact]
    public async Task The_save_guard_rejects_cross_tenant_references_and_spoofed_tenant_ids_on_every_new_entity()
    {
        await using var a = await CampaignWorld.OpenAsync(app); await using var b = await CampaignWorld.OpenAsync(app, freshTenant: true);
        var ac = await a.ContactAsync(); var bc = await b.ContactAsync();
        var acamp = await a.CreateAsync([ac]); await a.StartAsync(acamp.Id); await a.TickAsync(acamp.Id);
        var bcamp = await b.CreateAsync([bc]); await b.StartAsync(bcamp.Id); await b.TickAsync(bcamp.Id);
        var arec = await app.Db(db => db.CampaignRecipients.IgnoreQueryFilters().SingleAsync(r => r.CampaignId == acamp.Id));
        var brec = await app.Db(db => db.CampaignRecipients.IgnoreQueryFilters().SingleAsync(r => r.CampaignId == bcamp.Id));
        var now = DateTime.UtcNow;

        async Task Refused(Func<Microsoft.EntityFrameworkCore.DbContext, Task> add, string what)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => app.Db(async db => { await add(db); await db.SaveChangesAsync(); }));
            Assert.True(ex.ToString().Contains("another tenant", StringComparison.OrdinalIgnoreCase) || ex.GetType().Name.Contains("CrossTenant"), $"{what}: {ex.Message}");
        }
        // tenant A's rows pointing at tenant B's records
        await Refused(db => { db.Add(CampaignRecipient.Create(a.TenantId, acamp.Id, arec.SnapshotId, bc.ContactId, bc.LeadId, 50, "+1", "{}", now)); return Task.CompletedTask; }, "recipient → B's contact");
        await Refused(db => { db.Add(CampaignRecipient.Create(a.TenantId, bcamp.Id, brec.SnapshotId, ac.ContactId, ac.LeadId, 51, "+1", "{}", now)); return Task.CompletedTask; }, "recipient → B's campaign");
        await Refused(db => { db.Add(CampaignExecutionLog.Create(a.TenantId, bcamp.Id, ExecutionLogLevel.Info, "x", "x", null, now)); return Task.CompletedTask; }, "log → B's campaign");
        await Refused(db => { db.Add(CampaignConversion.Create(a.TenantId, acamp.Id, brec.Id, bc.LeadId, ConversionKind.Reply, Guid.NewGuid(), null, now, now, now)); return Task.CompletedTask; }, "conversion → B's recipient");
        await Refused(db => { db.Add(CampaignUsageEntry.Create(a.TenantId, bcamp.Id, arec.Id, 9, now)); return Task.CompletedTask; }, "usage → B's campaign");
        await Refused(db => { db.Add(CampaignAudienceSnapshot.Start(a.TenantId, bcamp.Id, 1, "{}", null, now)); return Task.CompletedTask; }, "snapshot → B's campaign");
        await Refused(db => { db.Add(CampaignStatsRollup.Create(a.TenantId, bcamp.Id, now)); return Task.CompletedTask; }, "rollup → B's campaign");
        await Refused(db => { db.Add(Campaign.Create(a.TenantId, "x", CampaignType.Manual, null, null, now)); var c = (Campaign)db.ChangeTracker.Entries().Last().Entity; typeof(Campaign).GetProperty(nameof(Campaign.ParentCampaignId))!.SetValue(c, bcamp.Id); return Task.CompletedTask; }, "campaign → B's parent");
        // a tenant-less / wrong-tenant write from a tenant scope
        var tenantless = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var scope = a.App.World.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(a.TenantId);
            var db = scope.ServiceProvider.GetRequiredService<LeadPilot.Infrastructure.Persistence.AppDbContext>();
            db.CampaignExecutionLogs.Add(CampaignExecutionLog.Create(b.TenantId, bcamp.Id, ExecutionLogLevel.Info, "x", "spoofed", null, now));
            await db.SaveChangesAsync();
        });
        Assert.IsType<CrossTenantViolationException>(tenantless);
        // append-only rows cannot be edited or deleted
        var update = await Assert.ThrowsAnyAsync<Exception>(() => app.Db(async db => { var l = await db.CampaignExecutionLogs.IgnoreQueryFilters().FirstAsync(x => x.CampaignId == acamp.Id); db.Remove(l); await db.SaveChangesAsync(); }));
        Assert.Contains("append-only", update.ToString());
        // query filters hide B's rows from A's scope
        using var s2 = a.App.World.Services.CreateScope(); s2.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(a.TenantId);
        var adb = s2.ServiceProvider.GetRequiredService<LeadPilot.Infrastructure.Persistence.AppDbContext>();
        Assert.DoesNotContain(await adb.Campaigns.Select(c => c.Id).ToListAsync(), id => id == bcamp.Id);
        Assert.Empty(await adb.CampaignRecipients.Where(r => r.CampaignId == bcamp.Id).ToListAsync());
        Assert.Empty(await adb.CampaignConversions.Where(r => r.CampaignId == bcamp.Id).ToListAsync());
        Assert.Empty(await adb.CampaignExecutionLogs.Where(r => r.CampaignId == bcamp.Id).ToListAsync());
        Assert.Empty(await adb.CampaignStatsRollups.Where(r => r.CampaignId == bcamp.Id).ToListAsync());
        Assert.Empty(await adb.CampaignUsageEntries.Where(r => r.CampaignId == bcamp.Id).ToListAsync());
        Assert.Empty(await adb.CampaignAudienceSnapshots.Where(r => r.CampaignId == bcamp.Id).ToListAsync());
        Assert.Empty(await adb.CampaignTenantSettings.Where(r => r.TenantId == b.TenantId).ToListAsync());
    }

    [RequiresMySqlFact]
    public async Task The_campaign_consumer_dead_letters_a_forged_tenant_header_and_rolls_up_only_inside_the_right_tenant()
    {
        await using var a = await CampaignWorld.OpenAsync(app);
        var c = await a.ContactAsync(); var camp = await a.CreateAsync([c]); await a.StartAsync(camp.Id); await a.RunToEndAsync(camp.Id);
        var processor = a.Worker.Services.GetRequiredService<MessageProcessor>(); var rmq = a.Worker.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        var queue = MessagingTopology.Queues(rmq).Single(q => q.ConsumerName == "campaigns.events");
        EventEnvelope Env(Guid tenant) => new(Guid.NewGuid(), tenant, "corr", null, CampaignEventTypes.RecipientChanged, DateTime.UtcNow, 1, JsonSerializer.Serialize(new CampaignRecipientChangedPayload(camp.Id, Guid.NewGuid(), "Sent")));
        var forged = Env(W.B.Id);                                                                               // A's campaign id under B's verified header
        var outcome = await processor.ProcessAsync(queue, JsonSerializer.SerializeToUtf8Bytes(forged), WorkerHarness.Headers(forged), default);
        Assert.Equal(DeliveryAction.DeadLetter, outcome.Action); Assert.Contains("campaign_not_found_for_tenant", outcome.Reason);
        var mismatch = Env(a.TenantId); var h = WorkerHarness.Headers(mismatch); h[MessagingTopology.TenantHeader] = System.Text.Encoding.UTF8.GetBytes(W.B.Id.ToString());
        Assert.Equal(DeliveryAction.DeadLetter, (await processor.ProcessAsync(queue, JsonSerializer.SerializeToUtf8Bytes(mismatch), h, default)).Action);
        var good = Env(a.TenantId);
        Assert.Equal(DeliveryAction.Ack, (await processor.ProcessAsync(queue, JsonSerializer.SerializeToUtf8Bytes(good), WorkerHarness.Headers(good), default)).Action);
        Assert.Equal(DeliveryAction.Ack, (await processor.ProcessAsync(queue, JsonSerializer.SerializeToUtf8Bytes(good), WorkerHarness.Headers(good), default)).Action);   // redelivery is a no-op
        Assert.Equal(1, await app.Db(db => db.ProcessedMessages.IgnoreQueryFilters().CountAsync(p => p.MessageId == good.MessageId && p.ConsumerName == "campaigns.events")));
    }
}
