using LeadPilot.Domain.Agents;
using System.Reflection;
using LeadPilot.Application.Abstractions;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;
using LeadPilot.Domain.Messaging;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Domain.Workflows;
using LeadPilot.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    internal ITenantContext Tenant => tenant;

    /// <summary>Referenced by the global query filter. Null (no tenant) matches nothing: reads fail closed.</summary>
    internal Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<Role> TenantRoles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> TenantUserRoles => Set<UserRole>();
    public DbSet<UserTenant> UserTenants => Set<UserTenant>();
    public DbSet<TenantInvitation> TenantInvitations => Set<TenantInvitation>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    // CRM (Phase 3)
    public DbSet<LeadCategory> LeadCategories => Set<LeadCategory>();
    public DbSet<LeadStatus> LeadStatuses => Set<LeadStatus>();
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<PipelineStage> PipelineStages => Set<PipelineStage>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadSource> LeadSources => Set<LeadSource>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();
    public DbSet<SuppressionEntry> SuppressionEntries => Set<SuppressionEntry>();
    public DbSet<LeadActivity> LeadActivities => Set<LeadActivity>();
    public DbSet<LeadNote> LeadNotes => Set<LeadNote>();
    public DbSet<LeadTag> LeadTags => Set<LeadTag>();
    public DbSet<LeadTagMap> LeadTagMaps => Set<LeadTagMap>();
    public DbSet<LeadScore> LeadScores => Set<LeadScore>();
    public DbSet<ScoringRuleSet> ScoringRuleSets => Set<ScoringRuleSet>();
    public DbSet<ScoringRule> ScoringRules => Set<ScoringRule>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<OpportunityActivity> OpportunityActivities => Set<OpportunityActivity>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<ImportRowError> ImportRowErrors => Set<ImportRowError>();
    public DbSet<DiscoveryJob> DiscoveryJobs => Set<DiscoveryJob>();
    public DbSet<DiscoveryResult> DiscoveryResults => Set<DiscoveryResult>();
    public DbSet<DiscoveryConnectorConfig> DiscoveryConnectorConfigs => Set<DiscoveryConnectorConfig>();
    public DbSet<DiscoveryUsageEntry> DiscoveryUsageEntries => Set<DiscoveryUsageEntry>();
    public DbSet<DiscoveryCategoryMapping> DiscoveryCategoryMappings => Set<DiscoveryCategoryMapping>();
    public DbSet<DiscoveryLocationPreset> DiscoveryLocationPresets => Set<DiscoveryLocationPreset>();

    // Messaging (Phase 5)
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelAccount> ChannelAccounts => Set<ChannelAccount>();
    public DbSet<ChannelCredential> ChannelCredentials => Set<ChannelCredential>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationAssignment> ConversationAssignments => Set<ConversationAssignment>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<MessageStatusEvent> MessageStatusEvents => Set<MessageStatusEvent>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<ChannelAccountNotification> ChannelAccountNotifications => Set<ChannelAccountNotification>();

    // Campaigns (Phase 6)
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignAudienceSnapshot> CampaignAudienceSnapshots => Set<CampaignAudienceSnapshot>();
    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();
    public DbSet<CampaignExecutionLog> CampaignExecutionLogs => Set<CampaignExecutionLog>();
    public DbSet<CampaignStatsRollup> CampaignStatsRollups => Set<CampaignStatsRollup>();
    public DbSet<CampaignConversion> CampaignConversions => Set<CampaignConversion>();
    public DbSet<CampaignUsageEntry> CampaignUsageEntries => Set<CampaignUsageEntry>();
    public DbSet<CampaignTenantSettings> CampaignTenantSettings => Set<CampaignTenantSettings>();

    // AI engine and knowledge base (Phase 7)
    public DbSet<KnowledgeSource> KnowledgeSources => Set<KnowledgeSource>();
    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<EmbeddingRecord> EmbeddingRecords => Set<EmbeddingRecord>();
    public DbSet<RetrievalLog> RetrievalLogs => Set<RetrievalLog>();
    public DbSet<AiTenantSettings> AiTenantSettings => Set<AiTenantSettings>();
    public DbSet<AiUsageEntry> AiUsageEntries => Set<AiUsageEntry>();
    public DbSet<ConversationSummary> ConversationSummaries => Set<ConversationSummary>();
    public DbSet<AiInteraction> AiInteractions => Set<AiInteraction>();
    public DbSet<AiToolExecution> AiToolExecutions => Set<AiToolExecution>();
    public DbSet<SalesAgent> SalesAgents => Set<SalesAgent>();
    public DbSet<AgentQualificationQuestion> AgentQualificationQuestions => Set<AgentQualificationQuestion>();
    public DbSet<CatalogProduct> CatalogProducts => Set<CatalogProduct>();
    public DbSet<PricingPlan> PricingPlans => Set<PricingPlan>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<ConversationNote> ConversationNotes => Set<ConversationNote>();
    public DbSet<HandoffEvent> HandoffEvents => Set<HandoffEvent>();
    public DbSet<AgentTenantSettings> AgentTenantSettings => Set<AgentTenantSettings>();
    public DbSet<LeadQualification> LeadQualifications => Set<LeadQualification>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();

    // Workflows (Phase 9)
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<WorkflowTrigger> WorkflowTriggers => Set<WorkflowTrigger>();
    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();
    public DbSet<WorkflowExecution> WorkflowExecutions => Set<WorkflowExecution>();
    public DbSet<WorkflowExecutionLog> WorkflowExecutionLogs => Set<WorkflowExecutionLog>();
    public DbSet<WorkflowTenantSettings> WorkflowTenantSettings => Set<WorkflowTenantSettings>();
    public DbSet<WorkflowNotification> WorkflowNotifications => Set<WorkflowNotification>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HavePrecision(6);
        configurationBuilder.Properties<DateTime?>().HavePrecision(6);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(b =>
        {
            b.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(u => u.CreatedAtUtc).HasPrecision(6);
            b.Property(u => u.LastLoginAtUtc).HasPrecision(6);
        });

        builder.Entity<Tenant>(b =>
        {
            b.ToTable("Tenants");
            b.Property(t => t.Name).HasMaxLength(200).IsRequired();
            b.Property(t => t.Slug).HasMaxLength(63).IsRequired();
            b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(t => t.Industry).HasMaxLength(100);
            b.Property(t => t.Timezone).HasMaxLength(64).IsRequired();
            b.Property(t => t.Locale).HasMaxLength(16).IsRequired();
            b.Property(t => t.SuspensionReason).HasMaxLength(500);
            b.Property(t => t.CreatedBy).HasMaxLength(256);
            b.Property(t => t.UpdatedBy).HasMaxLength(256);
            b.HasIndex(t => t.Slug).IsUnique();
            b.HasIndex(t => t.Status);
        });

        builder.Entity<TenantSetting>(b =>
        {
            b.ToTable("TenantSettings");
            b.Property(x => x.Key).HasMaxLength(100).IsRequired();
            b.Property(x => x.Value).HasMaxLength(4000).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(256);
            b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        });

        builder.Entity<UserProfile>(b =>
        {
            b.ToTable("UserProfiles");
            b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(x => x.Phone).HasMaxLength(32);
            b.Property(x => x.CreatedBy).HasMaxLength(256);
            b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => x.UserId).IsUnique();
            b.HasOne<ApplicationUser>().WithOne().HasForeignKey<UserProfile>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Permission>(b =>
        {
            b.ToTable("Permissions");
            b.Property(x => x.Code).HasMaxLength(100).IsRequired();
            b.Property(x => x.Name).HasMaxLength(150).IsRequired();
            b.Property(x => x.Description).HasMaxLength(500);
            b.Property(x => x.Group).HasMaxLength(50).IsRequired();
            b.HasIndex(x => x.Code).IsUnique();
        });

        builder.Entity<Role>(b =>
        {
            b.ToTable("Roles");
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Description).HasMaxLength(500);
            b.Property(x => x.CreatedBy).HasMaxLength(256);
            b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        });

        builder.Entity<RolePermission>(b =>
        {
            b.ToTable("RolePermissions");
            b.HasIndex(x => new { x.RoleId, x.PermissionId }).IsUnique();
            b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<UserRole>(b =>
        {
            b.ToTable("UserRoles");
            b.HasIndex(x => new { x.TenantId, x.UserId, x.RoleId }).IsUnique();
            b.HasIndex(x => x.UserId);
            b.HasIndex(x => x.RoleId);
            b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserTenant>(b =>
        {
            b.ToTable("UserTenants");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            b.HasIndex(x => x.UserId);
            b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TenantInvitation>(b =>
        {
            b.ToTable("TenantInvitations");
            b.Property(x => x.Email).HasMaxLength(256).IsRequired();
            b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.CreatedBy).HasMaxLength(256);
            b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Email, x.Status });
            b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AuditLog>(b =>
        {
            b.ToTable("AuditLogs");
            b.Property(x => x.ActorEmail).HasMaxLength(256);
            b.Property(x => x.Action).HasMaxLength(100).IsRequired();
            b.Property(x => x.EntityType).HasMaxLength(100);
            b.Property(x => x.EntityId).HasMaxLength(64);
            b.Property(x => x.Summary).HasMaxLength(1000).IsRequired();
            b.Property(x => x.IpAddress).HasMaxLength(64);
            b.Property(x => x.CorrelationId).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
            b.HasIndex(x => new { x.TenantId, x.Action });
        });

        builder.Entity<RefreshToken>(b =>
        {
            b.ToTable("RefreshTokens");
            b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.ReplacedByTokenHash).HasMaxLength(64);
            b.Property(x => x.CreatedByIp).HasMaxLength(64);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => x.UserId);
            b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        CrmModel.Configure(builder);
        DiscoveryModel.Configure(builder);
        MessagingModel.Configure(builder);
        CampaignModel.Configure(builder);
        AiModel.Configure(builder);
        AgentsModel.Configure(builder);
        WorkflowModel.Configure(builder);

        // Every tenant-owned entity: mandatory FK to Tenant (restrict), indexed TenantId and the global tenant filter.
        foreach (var type in builder.Model.GetEntityTypes().Select(e => e.ClrType).Where(t => typeof(ITenantOwned).IsAssignableFrom(t)).ToList())
        {
            ApplyTenantOwnership.MakeGenericMethod(type).Invoke(this, [builder]);
        }
    }

    private static readonly MethodInfo ApplyTenantOwnership =
        typeof(AppDbContext).GetMethod(nameof(ConfigureTenantOwned), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private void ConfigureTenantOwned<T>(ModelBuilder builder) where T : class, ITenantOwned
    {
        var entity = builder.Entity<T>();
        entity.HasOne<Tenant>().WithMany().HasForeignKey(nameof(ITenantOwned.TenantId)).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(nameof(ITenantOwned.TenantId));
        entity.HasQueryFilter(e => EF.Property<Guid>(e, nameof(ITenantOwned.TenantId)) == CurrentTenantId);
    }
}

public static class DatabaseInitializer
{
    /// <summary>Applies pending migrations when Database:MigrateOnStartup is true (Development/docker). Production uses an explicit migration step.</summary>
    public static async Task MigrateIfConfiguredAsync(IServiceProvider services, IConfiguration config)
    {
        if (!config.GetValue<bool>("Database:MigrateOnStartup")) return;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }
}
