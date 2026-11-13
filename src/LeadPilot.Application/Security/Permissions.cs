namespace LeadPilot.Application.Security;

public static class PermissionCodes
{
    public const string LeadsView = "Leads.View";
    public const string LeadsCreate = "Leads.Create";
    public const string LeadsUpdate = "Leads.Update";
    public const string LeadsDelete = "Leads.Delete";
    public const string LeadsImport = "Leads.Import";
    public const string LeadsExport = "Leads.Export";
    public const string LeadsAssign = "Leads.Assign";
    public const string ConsentManage = "Consent.Manage";
    public const string SuppressionManage = "Suppression.Manage";
    public const string PipelinesManage = "Pipelines.Manage";
    public const string ScoringManage = "Scoring.Manage";
    public const string CampaignsView = "Campaigns.View";
    public const string CampaignsCreate = "Campaigns.Create";
    public const string CampaignsExecute = "Campaigns.Execute";
    public const string ConversationsView = "Conversations.View";
    public const string ConversationsReply = "Conversations.Reply";
    public const string AgentsManage = "Agents.Manage";
    public const string ReportsView = "Reports.View";
    public const string UsersManage = "Users.Manage";
    public const string SettingsManage = "Settings.Manage";
    public const string AuditView = "Audit.View";
    public const string DiscoveryView = "Discovery.View";
    public const string DiscoveryRun = "Discovery.Run";
    public const string DiscoveryReview = "Discovery.Review";
    public const string DiscoveryManage = "Discovery.Manage";
    public const string IntegrationsManage = "Integrations.Manage";
    // Platform (operator) permissions: grantable only by roles of the platform tenant.
    public const string TenantsManage = "Tenants.Manage";
    public const string PlatformSwitchTenant = "Platform.SwitchTenant";
}

public sealed record PermissionDefinition(string Code, string Name, string Group, string Description, bool IsPlatform = false);

/// <summary>The global permission catalog. Seeded idempotently at startup; new phases append here.</summary>
public static class PermissionCatalog
{
    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(PermissionCodes.LeadsView, "View leads", "Leads", "See leads and their details."),
        new(PermissionCodes.LeadsCreate, "Create leads", "Leads", "Add and import leads."),
        new(PermissionCodes.LeadsUpdate, "Update leads", "Leads", "Edit leads, notes and pipeline stage."),
        new(PermissionCodes.LeadsDelete, "Delete leads", "Leads", "Archive, restore and delete leads, notes and tags."),
        new(PermissionCodes.LeadsImport, "Import leads", "Leads", "Upload CSV/Excel files and review duplicates."),
        new(PermissionCodes.LeadsExport, "Export leads", "Leads", "Download lead data (audited)."),
        new(PermissionCodes.LeadsAssign, "Assign leads", "Leads", "Assign leads to team members."),
        new(PermissionCodes.ConsentManage, "Manage consent", "Compliance", "Record consent grants and revocations."),
        new(PermissionCodes.SuppressionManage, "Manage suppression list", "Compliance", "View, add and remove suppression entries."),
        new(PermissionCodes.PipelinesManage, "Manage pipelines and categories", "CRM setup", "Configure pipelines, stages, lead categories and statuses."),
        new(PermissionCodes.ScoringManage, "Manage lead scoring", "CRM setup", "Edit scoring rules and recalculate scores."),
        new(PermissionCodes.CampaignsView, "View campaigns", "Campaigns", "See campaigns and their results."),
        new(PermissionCodes.CampaignsCreate, "Create campaigns", "Campaigns", "Create and edit campaigns."),
        new(PermissionCodes.CampaignsExecute, "Run campaigns", "Campaigns", "Start, pause and stop campaigns."),
        new(PermissionCodes.ConversationsView, "View conversations", "Conversations", "Read conversations."),
        new(PermissionCodes.ConversationsReply, "Reply to conversations", "Conversations", "Send replies and take over from the AI agent."),
        new(PermissionCodes.AgentsManage, "Manage AI agents", "AI", "Configure AI agents and knowledge."),
        new(PermissionCodes.ReportsView, "View reports", "Reports", "See analytics and reports."),
        new(PermissionCodes.UsersManage, "Manage users and roles", "Administration", "Invite users, assign roles and edit custom roles."),
        new(PermissionCodes.SettingsManage, "Manage settings", "Administration", "Edit workspace profile and settings."),
        new(PermissionCodes.AuditView, "View audit log", "Administration", "Read the workspace audit trail."),
        new(PermissionCodes.DiscoveryView, "View discovery", "Discovery", "See discovery searches, their progress and their results."),
        new(PermissionCodes.DiscoveryRun, "Run discovery searches", "Discovery", "Start, cancel and retry discovery searches (counts against the connector's monthly limit)."),
        new(PermissionCodes.DiscoveryReview, "Review discovery results", "Discovery", "Approve results into the CRM or reject them."),
        new(PermissionCodes.DiscoveryManage, "Manage discovery setup", "Discovery", "Edit category search phrases, saved locations and starter packs."),
        new(PermissionCodes.IntegrationsManage, "Manage integrations", "Administration", "Store connector API keys and set monthly usage limits."),
        new(PermissionCodes.TenantsManage, "Manage tenants", "Platform", "Create, edit, suspend and activate tenants.", IsPlatform: true),
        new(PermissionCodes.PlatformSwitchTenant, "Switch into tenants", "Platform", "Operate inside any active tenant (audited).", IsPlatform: true),
    ];

    private static readonly HashSet<string> Codes = All.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);
    public static bool Exists(string code) => Codes.Contains(code);
    public static bool IsPlatform(string code) => All.Any(p => p.Code == code && p.IsPlatform);
    public static IEnumerable<string> TenantCodes => All.Where(p => !p.IsPlatform).Select(p => p.Code);
}

public sealed record SystemRoleDefinition(string Name, string Description, bool PlatformOnly, IReadOnlyList<string> Permissions);

/// <summary>Default system roles. Tenant roles are created in every customer tenant; platform roles only in the platform tenant.</summary>
public static class SystemRoles
{
    public const string PlatformOwner = "Platform Owner";
    public const string SaasAdministrator = "SaaS Administrator";
    public const string TenantAdministrator = "Tenant Administrator";
    public const string TenantStaff = "Tenant Staff";
    public const string SalesManager = "Sales Manager";
    public const string SalesAgent = "Sales Agent";
    public const string MarketingManager = "Marketing Manager";
    public const string ReadOnly = "Read-only";

    private static readonly string[] AllCodes = PermissionCatalog.All.Select(p => p.Code).ToArray();

    public static readonly IReadOnlyList<SystemRoleDefinition> All =
    [
        new(PlatformOwner, "Full control of the platform and every tenant.", true, AllCodes),
        new(SaasAdministrator, "Operates tenants and supports customers; read-only on customer business data.", true,
        [
            PermissionCodes.TenantsManage, PermissionCodes.PlatformSwitchTenant, PermissionCodes.UsersManage, PermissionCodes.SettingsManage, PermissionCodes.AuditView,
            PermissionCodes.ReportsView, PermissionCodes.LeadsView, PermissionCodes.CampaignsView, PermissionCodes.ConversationsView
        ]),
        new(TenantAdministrator, "Full control of this workspace.", false, PermissionCatalog.TenantCodes.ToArray()),
        new(TenantStaff, "Day-to-day work with leads and conversations.", false,
        [
            PermissionCodes.LeadsView, PermissionCodes.LeadsCreate, PermissionCodes.LeadsUpdate, PermissionCodes.CampaignsView,
            PermissionCodes.ConversationsView, PermissionCodes.ConversationsReply, PermissionCodes.ReportsView, PermissionCodes.DiscoveryView
        ]),
        new(SalesManager, "Manages the sales team, leads and conversations.", false,
        [
            PermissionCodes.LeadsView, PermissionCodes.LeadsCreate, PermissionCodes.LeadsUpdate, PermissionCodes.LeadsDelete, PermissionCodes.CampaignsView,
            PermissionCodes.ConversationsView, PermissionCodes.ConversationsReply, PermissionCodes.ReportsView,
            PermissionCodes.LeadsImport, PermissionCodes.LeadsExport, PermissionCodes.LeadsAssign, PermissionCodes.ConsentManage,
            PermissionCodes.SuppressionManage, PermissionCodes.PipelinesManage, PermissionCodes.ScoringManage,
            PermissionCodes.DiscoveryView, PermissionCodes.DiscoveryRun, PermissionCodes.DiscoveryReview, PermissionCodes.DiscoveryManage
        ]),
        new(SalesAgent, "Works leads and conversations.", false,
        [
            PermissionCodes.LeadsView, PermissionCodes.LeadsCreate, PermissionCodes.LeadsUpdate, PermissionCodes.ConversationsView, PermissionCodes.ConversationsReply,
            PermissionCodes.DiscoveryView
        ]),
        new(MarketingManager, "Plans and runs campaigns.", false,
        [
            PermissionCodes.LeadsView, PermissionCodes.CampaignsView, PermissionCodes.CampaignsCreate, PermissionCodes.CampaignsExecute,
            PermissionCodes.ConversationsView, PermissionCodes.ReportsView,
            PermissionCodes.LeadsExport, PermissionCodes.ConsentManage, PermissionCodes.SuppressionManage, PermissionCodes.DiscoveryView, PermissionCodes.DiscoveryRun
        ]),
        new(ReadOnly, "Can view everything operational but change nothing.", false,
        [
            PermissionCodes.LeadsView, PermissionCodes.CampaignsView, PermissionCodes.ConversationsView, PermissionCodes.ReportsView
        ]),
    ];

    public static IEnumerable<SystemRoleDefinition> For(bool platformTenant) => All.Where(r => r.PlatformOnly == platformTenant);
}

/// <summary>Immutable set of permission codes with the evaluation rules used everywhere.</summary>
public sealed class PermissionSet
{
    public static readonly PermissionSet Empty = new([]);
    private readonly HashSet<string> _codes;

    public PermissionSet(IEnumerable<string> codes) => _codes = new HashSet<string>(codes, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Codes => _codes;
    public bool Has(string code) => _codes.Contains(code);
    public bool HasAll(IEnumerable<string> codes) => codes.All(_codes.Contains);

    /// <summary>Union of the permissions of several roles.</summary>
    public static PermissionSet Union(IEnumerable<IEnumerable<string>> perRole) => new(perRole.SelectMany(p => p));

    /// <summary>Codes in <paramref name="requested"/> that the actor holds no permission for (privilege-escalation guard).</summary>
    public IReadOnlyList<string> Ungrantable(IEnumerable<string> requested) => requested.Where(c => !_codes.Contains(c)).Distinct().ToList();

    /// <summary>Platform permissions may only be placed on roles of the platform tenant; unknown codes are never valid.</summary>
    public static IReadOnlyList<string> InvalidFor(IEnumerable<string> requested, bool roleIsInPlatformTenant) =>
        requested.Where(c => !PermissionCatalog.Exists(c) || (!roleIsInPlatformTenant && PermissionCatalog.IsPlatform(c))).Distinct().ToList();
}
