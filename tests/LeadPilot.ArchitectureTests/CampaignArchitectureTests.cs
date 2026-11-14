using System.Reflection;
using Hangfire;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Common;
using LeadPilot.Infrastructure.Campaigns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace LeadPilot.ArchitectureTests;

/// <summary>Rules that keep the Phase 6 campaign module on the single send pipeline, inside tenancy, behind permissions, and on the one scheduler.</summary>
public class CampaignArchitectureTests
{
    private static readonly Assembly Infrastructure = typeof(CampaignDirectory).Assembly;
    private static readonly Assembly Web = typeof(LeadPilot.Web.Models.ErrorViewModel).Assembly;
    private static readonly Assembly Api = typeof(LeadPilot.Api.Controllers.AuthController).Assembly;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeadPilotAI.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains("Migrations"));

    [Fact]
    public void Every_campaign_entity_is_tenant_owned_and_logs_and_usage_are_append_only()
    {
        var entities = typeof(Campaign).Assembly.GetTypes().Where(t => t.Namespace == typeof(Campaign).Namespace && typeof(Entity).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.True(entities.Count >= 8, "expected the campaign entities to be discovered");
        Assert.All(entities, t => Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), $"{t.Name} must be tenant owned"));
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(CampaignExecutionLog)));
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(CampaignUsageEntry)));
    }

    [Fact]
    public void Only_the_phase_5_send_runner_calls_the_gateway_send_methods()
    {
        string[] allowed = ["Messaging/Services/OutboundServices.cs", "Messaging/Waha/WahaGateway.cs", "Messaging/Gateway.cs"];
        var offenders = new List<string>();
        foreach (var project in new[] { "LeadPilot.Domain", "LeadPilot.Application", "LeadPilot.Web", "LeadPilot.Api", "LeadPilot.Worker", "LeadPilot.Shared", "LeadPilot.Infrastructure" })
            foreach (var file in Sources(project))
            {
                var rel = file.Replace('\\', '/');
                if (allowed.Any(a => rel.EndsWith(a, StringComparison.Ordinal))) continue;
                var text = File.ReadAllText(file);
                if (text.Contains(".SendTextAsync(") || text.Contains(".SendTemplateAsync(")) offenders.Add(rel);
            }
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_campaign_module_never_touches_the_gateway_port_or_waha()
    {
        var offenders = Sources("LeadPilot.Infrastructure").Where(f => f.Replace('\\', '/').Contains("/Campaigns/")).Where(f => { var t = File.ReadAllText(f); return t.Contains("IWhatsAppGateway") || t.Contains("WahaGateway") || t.Contains("HttpClient"); }).ToList();
        Assert.Empty(offenders);
        offenders = Sources("LeadPilot.Application").Where(f => f.Replace('\\', '/').Contains("/Campaigns/")).Where(f => File.ReadAllText(f).Contains("IWhatsAppGateway")).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Hangfire_is_the_only_scheduler_no_timers_threads_or_other_schedulers_in_the_campaign_module()
    {
        string[] forbidden = ["System.Threading.Timer", "new Timer(", "PeriodicTimer", "Quartz", "BackgroundService", "IHostedService", "Thread.Sleep", "new Thread("];
        var offenders = Sources("LeadPilot.Infrastructure").Where(f => f.Replace('\\', '/').Contains("/Campaigns/")).SelectMany(f => forbidden.Where(File.ReadAllText(f).Contains).Select(x => $"{Path.GetFileName(f)}: {x}")).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Hangfire_campaign_jobs_declare_their_queue_and_a_bounded_retry()
    {
        foreach (var t in new[] { typeof(CampaignJob), typeof(CampaignSweepJob) })
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.Equal(CampaignQueues.Campaigns, m.GetCustomAttribute<QueueAttribute>()?.Queue);
                var retry = m.GetCustomAttribute<AutomaticRetryAttribute>();
                Assert.NotNull(retry); Assert.InRange(retry!.Attempts, 0, 3);
            }
    }

    [Fact]
    public void Campaign_channels_have_exactly_one_implementation_and_no_fakes_ship()
    {
        var impls = Infrastructure.GetTypes().Where(t => typeof(ICampaignChannel).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false }).ToList();
        Assert.Single(impls); Assert.Equal(CampaignChannel.WhatsApp, ((ICampaignChannel)Activator.CreateInstance(impls[0], [null, null])!).Channel);
        Assert.DoesNotContain(Infrastructure.GetTypes(), t => t.Namespace?.Contains("Campaigns") == true && (t.Name.StartsWith("Fake") || t.Name.StartsWith("Mock") || t.Name.Contains("Email")));
    }

    [Fact]
    public void Web_and_Api_never_touch_the_database_context_for_campaigns()
    {
        foreach (var project in new[] { "LeadPilot.Web", "LeadPilot.Api" })
            Assert.DoesNotContain(Sources(project).Where(f => Path.GetFileName(f).Contains("Campaign")), f => File.ReadAllText(f).Contains("AppDbContext"));
    }

    public static IEnumerable<object[]> CampaignControllers() =>
        Web.GetTypes().Where(t => typeof(Controller).IsAssignableFrom(t) && t.Name is "CampaignsController" or "CampaignSettingsController").Select(t => new object[] { t })
            .Concat(Api.GetTypes().Where(t => t.Name == "CampaignsController").Select(t => new object[] { t }));

    [Theory, MemberData(nameof(CampaignControllers))]
    public void Every_campaign_action_has_a_catalog_permission_and_an_explicit_verb_and_mutations_are_post_only(Type controller)
    {
        var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null).ToList();
        Assert.NotEmpty(actions);
        var classPolicy = controller.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy).FirstOrDefault(p => p is not null);
        foreach (var a in actions)
        {
            var policy = a.GetCustomAttributes<AuthorizeAttribute>().Select(x => x.Policy).FirstOrDefault(p => p is not null) ?? classPolicy;
            Assert.True(policy is not null && PermissionCatalog.Exists(policy), $"{controller.Name}.{a.Name} needs a catalog permission");
            var verbs = a.GetCustomAttributes().OfType<HttpMethodAttribute>().SelectMany(v => v.HttpMethods).ToList();
            Assert.True(verbs.Count > 0, $"{controller.Name}.{a.Name} needs an explicit HTTP verb");
            var name = a.Name;
            if (new[] { "Create", "Update", "Delete", "Submit", "Approve", "Pause", "Resume", "Cancel", "Start", "Clone", "TestSend", "DryRun", "Unschedule", "Save", "KillSwitch", "RejectCampaign", "Reject" }.Contains(name) && controller.Namespace!.Contains("Web"))
                Assert.Equal(["POST"], verbs);
        }
    }

    [Fact]
    public void The_new_permissions_are_in_the_catalog_and_read_only_gets_nothing_but_view()
    {
        string[] codes = [PermissionCodes.CampaignsUpdate, PermissionCodes.CampaignsApprove, PermissionCodes.CampaignsCancel, PermissionCodes.CampaignsDelete, PermissionCodes.CampaignsSettings, PermissionCodes.AudiencesPreview];
        Assert.All(codes, c => Assert.True(PermissionCatalog.Exists(c)));
        var ro = SystemRoles.All.Single(r => r.Name == SystemRoles.ReadOnly).Permissions;
        Assert.All(codes, c => Assert.DoesNotContain(c, ro));
        Assert.Contains(PermissionCodes.CampaignsView, ro);
        var admin = SystemRoles.All.Single(r => r.Name == SystemRoles.TenantAdministrator).Permissions;
        Assert.All(codes, c => Assert.Contains(c, admin));
        // approving is not a marketing-manager default: with the four-eyes policy the approver is another person
        Assert.DoesNotContain(PermissionCodes.CampaignsApprove, SystemRoles.All.Single(r => r.Name == SystemRoles.MarketingManager).Permissions);
        Assert.DoesNotContain(PermissionCodes.CampaignsSettings, SystemRoles.All.Single(r => r.Name == SystemRoles.MarketingManager).Permissions);
    }

    [Fact]
    public void The_domain_status_machine_covers_every_status()
    {
        foreach (var s in Enum.GetValues<CampaignStatus>()) Assert.NotNull(CampaignStateMachine.Allowed(s));
        Assert.Equal(Enum.GetValues<CampaignStatus>().Length, new[] { CampaignStatus.Draft, CampaignStatus.PendingApproval, CampaignStatus.Scheduled, CampaignStatus.Running, CampaignStatus.Paused, CampaignStatus.Completed, CampaignStatus.Cancelled, CampaignStatus.Failed }.Length);
    }
}
