using LeadPilot.Domain.Config;
using LeadPilot.Domain.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>EF mapping of the Phase 13 configuration tables (custom fields, industry packs).</summary>
internal static class ConfigModel
{
    private static PropertyBuilder<TEnum> Str<TEnum>(this PropertyBuilder<TEnum> p, int max = 30) where TEnum : struct, Enum => p.HasConversion<string>().HasMaxLength(max);

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<CustomFieldDefinition>(b =>
        {
            b.ToTable("CustomFieldDefinitions");
            b.Property(x => x.EntityType).Str(20); b.Property(x => x.Type).Str(20);
            b.Property(x => x.Key).HasMaxLength(40).IsRequired(); b.Property(x => x.Label).HasMaxLength(100).IsRequired();
            b.Property(x => x.OptionsJson).HasColumnType("text"); b.Property(x => x.Pattern).HasMaxLength(200); b.Property(x => x.HelpText).HasMaxLength(200);
            b.Property(x => x.MinValue).HasPrecision(18, 4); b.Property(x => x.MaxValue).HasPrecision(18, 4);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.Ignore(x => x.Options);
            b.HasIndex(x => new { x.TenantId, x.EntityType, x.Key }).IsUnique().HasDatabaseName("UX_CustomFieldDefinitions_Key");
        });

        builder.Entity<CustomFieldValue>(b =>
        {
            b.ToTable("CustomFieldValues");
            b.Property(x => x.EntityType).Str(20);
            b.Property(x => x.TextValue).HasMaxLength(600); b.Property(x => x.NumberValue).HasPrecision(18, 4); b.Property(x => x.DateValue).HasColumnType("date");
            b.Property(x => x.SearchText).HasMaxLength(600).IsRequired(); b.Property(x => x.Display).HasMaxLength(600).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.FieldId, x.EntityId }).IsUnique().HasDatabaseName("UX_CustomFieldValues_Field_Entity");
            b.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            b.HasOne<CustomFieldDefinition>().WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PackDefinition>(b =>
        {
            b.ToTable("PackDefinitions");
            b.Property(x => x.Key).HasMaxLength(60).IsRequired(); b.Property(x => x.Name).HasMaxLength(150).IsRequired(); b.Property(x => x.Description).HasMaxLength(1000);
            b.Property(x => x.Industry).HasMaxLength(60); b.Property(x => x.Scope).Str(20); b.Property(x => x.ScopeKey).HasMaxLength(40).IsRequired();
            b.Property(x => x.ContentJson).HasColumnType("longtext").IsRequired(); b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.Key, x.VersionNumber, x.ScopeKey }).IsUnique().HasDatabaseName("UX_PackDefinitions_Key_Version_Scope");
            b.HasIndex(x => new { x.OwnerTenantId, x.Key });
            b.HasOne<LeadPilot.Domain.Tenancy.Tenant>().WithMany().HasForeignKey(x => x.OwnerTenantId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<TenantPackApplication>(b =>
        {
            b.ToTable("TenantPackApplications");
            b.Property(x => x.PackKey).HasMaxLength(60).IsRequired(); b.Property(x => x.PackName).HasMaxLength(150).IsRequired(); b.Property(x => x.ScopeKey).HasMaxLength(40).IsRequired();
            b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired(); b.Property(x => x.Mode).Str(20); b.Property(x => x.ConflictPolicy).HasMaxLength(20).IsRequired();
            b.Property(x => x.SummaryJson).HasColumnType("text").IsRequired(); b.Property(x => x.AppliedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.PackKey, x.AppliedAtUtc });
        });

        builder.Entity<PackItemLink>(b =>
        {
            b.ToTable("PackItemLinks");
            b.Property(x => x.PackKey).HasMaxLength(60).IsRequired(); b.Property(x => x.ItemType).HasMaxLength(30).IsRequired(); b.Property(x => x.ItemKey).HasMaxLength(200).IsRequired();
            b.Property(x => x.AppliedHash).HasMaxLength(64).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.PackKey, x.ItemType, x.ItemKey }).IsUnique().HasDatabaseName("UX_PackItemLinks_Item");
        });

        builder.Entity<LeadCategory>().HasOne<LeadCategory>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}
