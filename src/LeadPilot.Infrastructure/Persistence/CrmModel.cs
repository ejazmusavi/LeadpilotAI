using LeadPilot.Domain.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>EF mapping of the Phase 3 CRM tables. Tenant ownership (FK, index, query filter) is applied afterwards for every ITenantOwned entity.</summary>
internal static class CrmModel
{
    private static PropertyBuilder<TEnum> Str<TEnum>(this PropertyBuilder<TEnum> p, int max = 30) where TEnum : struct, Enum => p.HasConversion<string>().HasMaxLength(max);

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<LeadCategory>(b =>
        {
            b.ToTable("LeadCategories");
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.NormalizedName).HasMaxLength(100).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.NormalizedName }).IsUnique();
        });

        builder.Entity<LeadStatus>(b =>
        {
            b.ToTable("LeadStatuses");
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        });

        builder.Entity<Pipeline>(b =>
        {
            b.ToTable("Pipelines");
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        });

        builder.Entity<PipelineStage>(b =>
        {
            b.ToTable("PipelineStages");
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Kind).Str(20);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.PipelineId, x.Name }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.PipelineId, x.Position });
            b.HasOne<Pipeline>().WithMany().HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Lead>(b =>
        {
            b.ToTable("Leads");
            b.Property(x => x.Name).HasMaxLength(300).IsRequired();
            b.Property(x => x.NormalizedName).HasMaxLength(300).IsRequired();
            b.Property(x => x.DedupKey).HasMaxLength(Normalizer.MaxDedupKeyLength).IsRequired();
            b.Property(x => x.Country).HasMaxLength(100); b.Property(x => x.Province).HasMaxLength(100); b.Property(x => x.City).HasMaxLength(100);
            b.Property(x => x.District).HasMaxLength(100); b.Property(x => x.Area).HasMaxLength(100);
            b.Property(x => x.Address).HasMaxLength(500);
            b.Property(x => x.Website).HasMaxLength(500);
            b.Property(x => x.WebsiteDomain).HasMaxLength(255);
            b.Property(x => x.PrimarySourceType).Str();
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.DedupKey });
            b.HasIndex(x => new { x.TenantId, x.WebsiteDomain });
            b.HasIndex(x => new { x.TenantId, x.IsArchived, x.CreatedAtUtc });
            b.HasIndex(x => new { x.TenantId, x.PipelineStageId });
            b.HasIndex(x => new { x.TenantId, x.StatusId });
            b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
            b.HasIndex(x => new { x.TenantId, x.CategoryId });
            b.HasIndex(x => new { x.TenantId, x.City });
            b.HasIndex(x => new { x.TenantId, x.Score });
            b.HasIndex(x => new { x.TenantId, x.NormalizedName });
            b.HasOne<LeadCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Pipeline>().WithMany().HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PipelineStage>().WithMany().HasForeignKey(x => x.PipelineStageId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LeadStatus>().WithMany().HasForeignKey(x => x.StatusId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<LeadSource>(b =>
        {
            b.ToTable("LeadSources");
            b.Property(x => x.Type).Str();
            b.Property(x => x.ExternalId).HasMaxLength(200);
            b.Property(x => x.Name).HasMaxLength(200); b.Property(x => x.Url).HasMaxLength(500); b.Property(x => x.Attribution).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.Type, x.ExternalId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LeadId });
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Contact>(b =>
        {
            b.ToTable("Contacts");
            b.Property(x => x.Name).HasMaxLength(200); b.Property(x => x.Role).HasMaxLength(100);
            b.Property(x => x.PhoneRaw).HasMaxLength(40); b.Property(x => x.PhoneE164).HasMaxLength(20);
            b.Property(x => x.PhoneUniqueKey).HasMaxLength(20); b.Property(x => x.Email).HasMaxLength(256); b.Property(x => x.EmailUniqueKey).HasMaxLength(256);
            b.Property(x => x.DataOrigin).Str(20);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.LeadId, x.PhoneUniqueKey }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LeadId, x.EmailUniqueKey }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.PhoneE164 });
            b.HasIndex(x => new { x.TenantId, x.Email });
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ConsentRecord>(b =>
        {
            b.ToTable("ConsentRecords");
            b.Property(x => x.Channel).Str(20); b.Property(x => x.Purpose).Str(20); b.Property(x => x.Status).Str(20);
            b.Property(x => x.Source).HasMaxLength(200).IsRequired(); b.Property(x => x.EvidenceRef).HasMaxLength(500); b.Property(x => x.Note).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.ContactId, x.Channel, x.Purpose, x.OccurredAtUtc });
            b.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
            b.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SuppressionEntry>(b =>
        {
            b.ToTable("SuppressionEntries");
            b.Property(x => x.Channel).Str(20); b.Property(x => x.Reason).Str(20);
            b.Property(x => x.ValueNormalized).HasMaxLength(320).IsRequired(); b.Property(x => x.ValueHash).HasMaxLength(64).IsRequired(); b.Property(x => x.Note).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.Channel, x.ValueNormalized }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.ValueNormalized });
            b.HasIndex(x => new { x.TenantId, x.ValueHash });
        });

        builder.Entity<LeadActivity>(b =>
        {
            b.ToTable("LeadActivities");
            b.Property(x => x.Type).Str(); b.Property(x => x.Summary).HasMaxLength(500).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.LeadId, x.OccurredAtUtc });
            b.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<LeadNote>(b =>
        {
            b.ToTable("LeadNotes");
            b.Property(x => x.Body).HasMaxLength(4000).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.LeadId, x.CreatedAtUtc });
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<LeadTag>(b =>
        {
            b.ToTable("LeadTags");
            b.Property(x => x.Name).HasMaxLength(50).IsRequired(); b.Property(x => x.NormalizedName).HasMaxLength(50).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.NormalizedName }).IsUnique();
        });

        builder.Entity<LeadTagMap>(b =>
        {
            b.ToTable("LeadTagMaps");
            b.HasIndex(x => new { x.LeadId, x.TagId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.TagId });
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<LeadTag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LeadScore>(b =>
        {
            b.ToTable("LeadScores");
            b.HasIndex(x => new { x.TenantId, x.LeadId, x.CalculatedAtUtc });
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ScoringRuleSet>(b =>
        {
            b.ToTable("ScoringRuleSets");
            b.Property(x => x.Note).HasMaxLength(300);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Version }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.IsActive });
        });

        builder.Entity<ScoringRule>(b =>
        {
            b.ToTable("ScoringRules");
            b.Property(x => x.Dimension).Str(); b.Property(x => x.Target).HasMaxLength(200).IsRequired(); b.Property(x => x.Description).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.RuleSetId });
            b.HasOne<ScoringRuleSet>().WithMany().HasForeignKey(x => x.RuleSetId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Opportunity>(b =>
        {
            b.ToTable("Opportunities");
            b.Property(x => x.Name).HasMaxLength(200).IsRequired(); b.Property(x => x.Value).HasPrecision(18, 2); b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.Property(x => x.Status).Str(20);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.LeadId });
            b.HasIndex(x => new { x.TenantId, x.PipelineStageId });
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PipelineStage>().WithMany().HasForeignKey(x => x.PipelineStageId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<OpportunityActivity>(b =>
        {
            b.ToTable("OpportunityActivities");
            b.Property(x => x.Type).HasMaxLength(50).IsRequired(); b.Property(x => x.Summary).HasMaxLength(500).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.OpportunityId, x.OccurredAtUtc });
            b.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ImportJob>(b =>
        {
            b.ToTable("ImportJobs");
            b.Property(x => x.FileName).HasMaxLength(255).IsRequired(); b.Property(x => x.FileType).Str(10); b.Property(x => x.Status).Str(20);
            b.Property(x => x.DuplicatePolicy).Str(20); b.Property(x => x.StoredFileKey).HasMaxLength(400); b.Property(x => x.FailureReason).HasMaxLength(500);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        });

        builder.Entity<ImportRowError>(b =>
        {
            b.ToTable("ImportRowErrors");
            b.Property(x => x.Kind).Str(20); b.Property(x => x.Resolution).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Column).HasMaxLength(200); b.Property(x => x.Message).HasMaxLength(500).IsRequired(); b.Property(x => x.RowData).HasMaxLength(8000);
            b.HasIndex(x => new { x.TenantId, x.ImportJobId, x.RowNumber });
            b.HasIndex(x => new { x.TenantId, x.ImportJobId, x.Kind });
            b.HasOne<ImportJob>().WithMany().HasForeignKey(x => x.ImportJobId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.DuplicateLeadId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
