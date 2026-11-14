using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>EF mapping of the Phase 8 sales-agent tables. Tenant ownership (FK, index, query filter) is applied afterwards for every ITenantOwned entity.</summary>
internal static class AgentsModel
{
    private static PropertyBuilder<TEnum> Str<TEnum>(this PropertyBuilder<TEnum> p, int max = 30) where TEnum : struct, Enum => p.HasConversion<string>().HasMaxLength(max);

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<SalesAgent>(b =>
        {
            b.ToTable("SalesAgents");
            b.Property(x => x.Name).HasMaxLength(100).IsRequired(); b.Property(x => x.Description).HasMaxLength(500);
            b.Property(x => x.Instructions).HasColumnType("text").IsRequired(); b.Property(x => x.ChatModel).HasMaxLength(100);
            b.Property(x => x.MinConfidence).HasPrecision(5, 4); b.Property(x => x.EnabledToolsJson).HasColumnType("text").IsRequired();
            b.Property(x => x.OffTopicReply).HasMaxLength(500).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.IsDefault });
        });

        builder.Entity<AgentQualificationQuestion>(b =>
        {
            b.ToTable("AgentQualificationQuestions");
            b.Property(x => x.Key).HasMaxLength(40).IsRequired(); b.Property(x => x.Question).HasMaxLength(300).IsRequired();
            b.Property(x => x.AnswerType).Str(20); b.Property(x => x.ChoicesJson).HasColumnType("text");
            b.HasIndex(x => new { x.TenantId, x.AgentId, x.Key }).IsUnique();
            b.HasOne<SalesAgent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CatalogProduct>(b =>
        {
            b.ToTable("CatalogProducts");
            b.Property(x => x.Name).HasMaxLength(150).IsRequired(); b.Property(x => x.Summary).HasMaxLength(1000); b.Property(x => x.Features).HasColumnType("text");
            b.Property(x => x.DemoProcess).HasMaxLength(2000); b.Property(x => x.TrialProcess).HasMaxLength(2000);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.IsActive });
        });

        builder.Entity<PricingPlan>(b =>
        {
            b.ToTable("PricingPlans");
            b.Property(x => x.Name).HasMaxLength(100).IsRequired(); b.Property(x => x.Price).HasPrecision(18, 2); b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.Property(x => x.BillingPeriod).HasMaxLength(40); b.Property(x => x.Details).HasMaxLength(2000); b.Property(x => x.MaxDiscountPercent).HasPrecision(5, 2);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.ProductId });
            b.HasOne<CatalogProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ApprovalRequest>(b =>
        {
            b.ToTable("ApprovalRequests");
            b.Property(x => x.ToolName).HasMaxLength(60).IsRequired(); b.Property(x => x.Reason).HasMaxLength(200).IsRequired(); b.Property(x => x.Summary).HasMaxLength(500).IsRequired();
            b.Property(x => x.InputJson).HasColumnType("text").IsRequired(); b.Property(x => x.DedupeKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.Status).Str(20); b.Property(x => x.Execution).Str(20); b.Property(x => x.ExecutionNote).HasMaxLength(500); b.Property(x => x.DecisionNote).HasMaxLength(300);
            b.HasIndex(x => new { x.TenantId, x.DedupeKey }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.RequestedAtUtc });
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ConversationNote>(b =>
        {
            b.ToTable("ConversationNotes");
            b.Property(x => x.Body).HasMaxLength(2000).IsRequired(); b.Property(x => x.Author).Str(20);
            b.HasIndex(x => new { x.TenantId, x.ConversationId, x.CreatedAtUtc });
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<HandoffEvent>(b =>
        {
            b.ToTable("HandoffEvents");
            b.Property(x => x.Kind).Str(20); b.Property(x => x.Actor).Str(20);
            b.Property(x => x.Reason).HasMaxLength(300); b.Property(x => x.FromState).HasMaxLength(30); b.Property(x => x.ToState).HasMaxLength(30);
            b.HasIndex(x => new { x.TenantId, x.ConversationId, x.OccurredAtUtc });
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AgentTenantSettings>(b =>
        {
            b.ToTable("AgentTenantSettings");
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => x.TenantId).IsUnique();
        });

        builder.Entity<LeadQualification>(b =>
        {
            b.ToTable("LeadQualifications");
            b.Property(x => x.AnswersJson).HasColumnType("text").IsRequired(); b.Property(x => x.Fit).Str(20); b.Property(x => x.Summary).HasMaxLength(300);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.LeadId }).IsUnique();
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<FollowUp>(b =>
        {
            b.ToTable("FollowUps");
            b.Property(x => x.Note).HasMaxLength(500).IsRequired(); b.Property(x => x.Status).Str(20); b.Property(x => x.CreatedByKind).Str(20);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Status, x.DueAtUtc });
            b.HasIndex(x => new { x.TenantId, x.LeadId, x.Status });
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
