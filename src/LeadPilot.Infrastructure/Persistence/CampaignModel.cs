using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>EF mapping of the Phase 6 campaign tables. Tenant ownership (FK, index, query filter) is applied afterwards for every ITenantOwned entity.</summary>
internal static class CampaignModel
{
    private static PropertyBuilder<TEnum> Str<TEnum>(this PropertyBuilder<TEnum> p, int max = 30) where TEnum : struct, Enum => p.HasConversion<string>().HasMaxLength(max);

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<Campaign>(b =>
        {
            b.ToTable("Campaigns");
            b.Property(x => x.Name).HasMaxLength(150).IsRequired();
            b.Property(x => x.Description).HasMaxLength(500);
            b.Property(x => x.Type).Str(20); b.Property(x => x.Channel).Str(20); b.Property(x => x.Status).Str(20);
            b.Property(x => x.MessageBodySnapshot).HasColumnType("text"); b.Property(x => x.OptOutFooterSnapshot).HasMaxLength(200);
            b.Property(x => x.AudienceJson).HasColumnType("text").IsRequired();
            b.Property(x => x.ScheduleTimeZone).HasMaxLength(64); b.Property(x => x.ScheduleJobId).HasMaxLength(100);
            b.Property(x => x.RejectedReason).HasMaxLength(500); b.Property(x => x.PauseReason).HasMaxLength(300); b.Property(x => x.FailureReason).HasMaxLength(500);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.Status, x.ScheduledAtUtc });
            b.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
            b.HasIndex(x => new { x.TenantId, x.ParentCampaignId });
            b.HasOne<MessageTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ChannelAccount>().WithMany().HasForeignKey(x => x.ChannelAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Campaign>().WithMany().HasForeignKey(x => x.ParentCampaignId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CampaignAudienceSnapshot>(b =>
        {
            b.ToTable("CampaignAudienceSnapshots");
            b.Property(x => x.DefinitionJson).HasColumnType("text").IsRequired();
            b.Property(x => x.ExclusionBreakdownJson).HasColumnType("text").IsRequired();
            b.Property(x => x.Status).Str(20);
            b.HasIndex(x => new { x.TenantId, x.CampaignId });
            b.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CampaignRecipient>(b =>
        {
            b.ToTable("CampaignRecipients");
            b.Property(x => x.Destination).HasMaxLength(32);
            b.Property(x => x.Status).Str(20);
            b.Property(x => x.StatusReason).HasMaxLength(60); b.Property(x => x.StatusDetail).HasMaxLength(500);
            b.Property(x => x.EligibilityJson).HasColumnType("text").IsRequired();
            b.Property(x => x.Version).IsConcurrencyToken();
            // a contact can never be queued twice in one campaign (database-enforced)
            b.HasIndex(x => new { x.CampaignId, x.ContactId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.CampaignId, x.Status, x.Sequence });
            b.HasIndex(x => new { x.TenantId, x.ContactId, x.QueuedAtUtc });
            b.HasIndex(x => new { x.TenantId, x.MessageId });
            b.HasIndex(x => new { x.TenantId, x.LeadId });
            b.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CampaignAudienceSnapshot>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CampaignExecutionLog>(b =>
        {
            b.ToTable("CampaignExecutionLogs");
            b.Property(x => x.Level).Str(20); b.Property(x => x.Code).HasMaxLength(60).IsRequired(); b.Property(x => x.Message).HasMaxLength(1000).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.CampaignId, x.OccurredAtUtc });
            b.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CampaignStatsRollup>(b =>
        {
            b.ToTable("CampaignStatsRollups");
            b.Ignore(x => x.Messaged); b.Ignore(x => x.DeliveredOrBetter);
            b.HasIndex(x => x.CampaignId).IsUnique();
            b.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CampaignConversion>(b =>
        {
            b.ToTable("CampaignConversions");
            b.Property(x => x.Kind).Str(20); b.Property(x => x.Detail).HasMaxLength(300);
            b.HasIndex(x => new { x.TenantId, x.RecipientId, x.Kind, x.ReferenceId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.CampaignId, x.OccurredAtUtc });
            b.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CampaignRecipient>().WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CampaignUsageEntry>(b =>
        {
            b.ToTable("CampaignUsageEntries");
            b.HasIndex(x => new { x.TenantId, x.RecipientId, x.Round }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.PeriodKey });
            b.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CampaignRecipient>().WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CampaignTenantSettings>(b =>
        {
            b.ToTable("CampaignTenantSettings");
            b.Property(x => x.OptOutFooter).HasMaxLength(200).IsRequired();
            b.Property(x => x.TestNumbersJson).HasColumnType("text").IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => x.TenantId).IsUnique();
        });

        builder.Entity<Message>(b =>
        {
            b.HasIndex(x => new { x.TenantId, x.CampaignId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.CampaignRecipientId });
            b.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
