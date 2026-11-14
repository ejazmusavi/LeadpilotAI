using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>EF mapping of the Phase 4 discovery tables. Tenant ownership (FK, index, query filter) is applied afterwards for every ITenantOwned entity.</summary>
internal static class DiscoveryModel
{
    private static PropertyBuilder<TEnum> Str<TEnum>(this PropertyBuilder<TEnum> p, int max = 30) where TEnum : struct, Enum => p.HasConversion<string>().HasMaxLength(max);

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<DiscoveryJob>(b =>
        {
            b.ToTable("DiscoveryJobs");
            b.Property(x => x.ConnectorType).HasMaxLength(50).IsRequired();
            b.Property(x => x.Status).Str(20);
            b.Property(x => x.Country).HasMaxLength(100); b.Property(x => x.Province).HasMaxLength(100); b.Property(x => x.City).HasMaxLength(100);
            b.Property(x => x.District).HasMaxLength(100); b.Property(x => x.Area).HasMaxLength(100);
            b.Property(x => x.Keywords).HasMaxLength(300); b.Property(x => x.SeedUrls).HasMaxLength(4000);
            b.Property(x => x.HangfireJobId).HasMaxLength(64); b.Property(x => x.Checkpoint).HasMaxLength(2000);
            b.Property(x => x.Warnings).HasMaxLength(1000); b.Property(x => x.ErrorCode).HasMaxLength(50); b.Property(x => x.ErrorMessage).HasMaxLength(500);
            b.Property(x => x.EstimatedCostUsd).HasPrecision(18, 6);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.HasOne<LeadCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DiscoveryResult>(b =>
        {
            b.ToTable("DiscoveryResults");
            b.Property(x => x.ConnectorType).HasMaxLength(50).IsRequired();
            b.Property(x => x.ExternalId).HasMaxLength(300).IsRequired();
            b.Property(x => x.Name).HasMaxLength(300); b.Property(x => x.CategoryLabel).HasMaxLength(300);
            b.Property(x => x.Country).HasMaxLength(100); b.Property(x => x.Province).HasMaxLength(100); b.Property(x => x.City).HasMaxLength(100);
            b.Property(x => x.District).HasMaxLength(100); b.Property(x => x.Area).HasMaxLength(100);
            b.Property(x => x.Address).HasMaxLength(500); b.Property(x => x.Website).HasMaxLength(500);
            b.Property(x => x.PublicPhone).HasMaxLength(40); b.Property(x => x.PublicEmail).HasMaxLength(256);
            b.Property(x => x.SocialLinks).HasMaxLength(2000);
            b.Property(x => x.SourceName).HasMaxLength(200); b.Property(x => x.SourceUrl).HasMaxLength(500); b.Property(x => x.Attribution).HasMaxLength(500);
            b.Property(x => x.ResearchNotes).HasMaxLength(2000); b.Property(x => x.Provenance).HasMaxLength(2000);
            b.Property(x => x.ReviewStatus).IsConcurrencyToken(); b.Property(x => x.ConfidenceLevel).Str(10); b.Property(x => x.Enrichment).Str(20); b.Property(x => x.ReviewStatus).Str(20);
            b.Property(x => x.Approval).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ReviewNote).HasMaxLength(500); b.Property(x => x.DuplicateReasons).HasMaxLength(300);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.ConnectorType, x.ExternalId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.JobId, x.ReviewStatus });
            b.HasIndex(x => new { x.TenantId, x.ReviewStatus, x.Confidence });
            b.HasIndex(x => new { x.ExpiresAtUtc, x.ContentPurged });
            b.HasOne<DiscoveryJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.DuplicateLeadId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LeadCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DiscoveryConnectorConfig>(b =>
        {
            b.ToTable("DiscoveryConnectorConfigs");
            b.Property(x => x.ConnectorType).HasMaxLength(50).IsRequired();
            b.Property(x => x.ProtectedApiKey).HasMaxLength(2000); b.Property(x => x.KeyHint).HasMaxLength(20);
            b.Property(x => x.MonthlyBudgetUsd).HasPrecision(18, 2); b.Property(x => x.CostPerCallUsd).HasPrecision(18, 6);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.ConnectorType }).IsUnique();
        });

        builder.Entity<DiscoveryUsageEntry>(b =>
        {
            b.ToTable("DiscoveryUsageEntries");
            b.Property(x => x.ConnectorType).HasMaxLength(50).IsRequired(); b.Property(x => x.Operation).HasMaxLength(100).IsRequired();
            b.Property(x => x.EstimatedCostUsd).HasPrecision(18, 6);
            b.HasIndex(x => new { x.TenantId, x.ConnectorType, x.PeriodKey });
            b.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
        });

        builder.Entity<DiscoveryCategoryMapping>(b =>
        {
            b.ToTable("DiscoveryCategoryMappings");
            b.Property(x => x.SearchTerms).HasMaxLength(600).IsRequired(); b.Property(x => x.PlaceType).HasMaxLength(60);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.CategoryId }).IsUnique();
            b.HasOne<LeadCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DiscoveryLocationPreset>(b =>
        {
            b.ToTable("DiscoveryLocationPresets");
            b.Property(x => x.Country).HasMaxLength(100).IsRequired(); b.Property(x => x.Province).HasMaxLength(100); b.Property(x => x.City).HasMaxLength(100);
            b.Property(x => x.District).HasMaxLength(100); b.Property(x => x.Area).HasMaxLength(100); b.Property(x => x.Label).HasMaxLength(500).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Label }).IsUnique();
        });
    }
}
