using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>EF mapping of the Phase 7 AI tables. Tenant ownership (FK, index, query filter) is applied afterwards for every ITenantOwned entity.</summary>
internal static class AiModel
{
    private static PropertyBuilder<TEnum> Str<TEnum>(this PropertyBuilder<TEnum> p, int max = 30) where TEnum : struct, Enum => p.HasConversion<string>().HasMaxLength(max);

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<KnowledgeSource>(b =>
        {
            b.ToTable("KnowledgeSources");
            b.Property(x => x.Name).HasMaxLength(150).IsRequired();
            b.Property(x => x.Url).HasMaxLength(500);
            b.Property(x => x.Type).Str(20); b.Property(x => x.Status).Str(20);
            b.Property(x => x.LastSyncError).HasMaxLength(500);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Type });
        });

        builder.Entity<KnowledgeDocument>(b =>
        {
            b.ToTable("KnowledgeDocuments");
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.Kind).Str(20); b.Property(x => x.Status).Str(20);
            b.Property(x => x.SourceUrl).HasMaxLength(500);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.SourceId });
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.SourceId, x.SourceUrl });
            b.HasOne<KnowledgeSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DocumentVersion>(b =>
        {
            b.ToTable("DocumentVersions");
            b.Property(x => x.Content).HasColumnType("longtext").IsRequired();
            b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.FileName).HasMaxLength(260); b.Property(x => x.MimeType).HasMaxLength(100);
            b.Property(x => x.Status).Str(20);
            b.Property(x => x.Error).HasMaxLength(500); b.Property(x => x.EmbeddingModel).HasMaxLength(100); b.Property(x => x.CreatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DocumentChunk>(b =>
        {
            b.ToTable("DocumentChunks");
            b.Property(x => x.Text).HasColumnType("text").IsRequired();
            b.Property(x => x.Heading).HasMaxLength(200);
            b.HasIndex(x => new { x.VersionId, x.Ordinal }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.DocumentId });
            b.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<EmbeddingRecord>(b =>
        {
            b.ToTable("EmbeddingRecords");
            b.Property(x => x.Provider).HasMaxLength(30).IsRequired(); b.Property(x => x.Model).HasMaxLength(100).IsRequired(); b.Property(x => x.StoreName).HasMaxLength(30).IsRequired();
            b.Property(x => x.Vector).HasColumnType("longblob").IsRequired();
            b.HasIndex(x => new { x.ChunkId, x.Model }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Model });
            b.HasIndex(x => new { x.TenantId, x.DocumentId });
            b.HasOne<DocumentChunk>().WithMany().HasForeignKey(x => x.ChunkId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RetrievalLog>(b =>
        {
            b.ToTable("RetrievalLogs");
            b.Property(x => x.Source).Str(20); b.Property(x => x.Outcome).Str(20);
            b.Property(x => x.QueryHash).HasMaxLength(64).IsRequired(); b.Property(x => x.QueryText).HasMaxLength(1000);
            b.Property(x => x.MinScore).HasPrecision(5, 4); b.Property(x => x.TopScore).HasPrecision(5, 4);
            b.Property(x => x.ResultsJson).HasColumnType("text").IsRequired();
            b.Property(x => x.EmbeddingModel).HasMaxLength(100); b.Property(x => x.StoreName).HasMaxLength(30).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
            b.HasIndex(x => new { x.TenantId, x.ConversationId });
        });

        builder.Entity<AiTenantSettings>(b =>
        {
            b.ToTable("AiTenantSettings");
            b.Property(x => x.ProtectedApiKey).HasMaxLength(2000); b.Property(x => x.KeyHint).HasMaxLength(20);
            b.Property(x => x.ProtectedEmbeddingKey).HasMaxLength(2000); b.Property(x => x.EmbeddingKeyHint).HasMaxLength(20);
            b.Property(x => x.ChatModel).HasMaxLength(100).IsRequired(); b.Property(x => x.ClassifierModel).HasMaxLength(100).IsRequired();
            b.Property(x => x.SummaryModel).HasMaxLength(100).IsRequired(); b.Property(x => x.EmbeddingModel).HasMaxLength(100).IsRequired();
            b.Property(x => x.MinRetrievalScore).HasPrecision(5, 4); b.Property(x => x.MinConfidence).HasPrecision(5, 4);
            b.Property(x => x.MonthlyBudgetUsd).HasPrecision(18, 2);
            b.Property(x => x.UnsupportedReply).HasMaxLength(500).IsRequired(); b.Property(x => x.ToneInstructions).HasMaxLength(1000); b.Property(x => x.EscalationKeywords).HasMaxLength(2000);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => x.TenantId).IsUnique();
        });

        builder.Entity<AiUsageEntry>(b =>
        {
            b.ToTable("AiUsageEntries");
            b.Property(x => x.Operation).HasMaxLength(40).IsRequired(); b.Property(x => x.Provider).HasMaxLength(30).IsRequired(); b.Property(x => x.Model).HasMaxLength(100).IsRequired();
            b.Property(x => x.ErrorCode).HasMaxLength(60);
            b.Property(x => x.EstimatedCostUsd).HasPrecision(18, 8);   // a single call costs fractions of a cent; the usual 2 decimals would round every entry to zero
            b.HasIndex(x => new { x.TenantId, x.PeriodKey });
            b.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
        });

        builder.Entity<ConversationSummary>(b =>
        {
            b.ToTable("ConversationSummaries");
            b.Property(x => x.Summary).HasColumnType("text").IsRequired();
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.ConversationId }).IsUnique();
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AiInteraction>(b =>
        {
            b.ToTable("AiInteractions");
            b.Property(x => x.Intent).HasMaxLength(60).IsRequired();
            b.Property(x => x.Decision).Str(20); b.Property(x => x.Status).Str(20);
            b.Property(x => x.Confidence).HasPrecision(5, 4);
            b.Property(x => x.DraftText).HasColumnType("text"); b.Property(x => x.CitationsJson).HasColumnType("text").IsRequired(); b.Property(x => x.ValidationJson).HasColumnType("text").IsRequired();
            b.Property(x => x.ExtractedJson).HasColumnType("text").IsRequired();
            b.Property(x => x.EscalationReason).HasMaxLength(200); b.Property(x => x.NextAction).HasMaxLength(300); b.Property(x => x.Model).HasMaxLength(100).IsRequired();
            // one AI run per inbound message: a redelivered event can never produce a second suggestion
            b.HasIndex(x => new { x.TenantId, x.TriggerMessageId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.ConversationId, x.CreatedAtUtc });
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAtUtc });
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Message>().WithMany().HasForeignKey(x => x.TriggerMessageId).OnDelete(DeleteBehavior.Restrict);
            b.Property(x => x.AutoSend).Str(20); b.Property(x => x.AutoSendNote).HasMaxLength(200);
        });

        builder.Entity<AiToolExecution>(b =>
        {
            b.ToTable("AiToolExecutions");
            b.Property(x => x.ToolName).HasMaxLength(60).IsRequired(); b.Property(x => x.ArgumentsSummary).HasMaxLength(500).IsRequired();
            b.Property(x => x.Outcome).Str(20); b.Property(x => x.Reason).HasMaxLength(200);
            b.Property(x => x.InputJson).HasColumnType("text"); b.Property(x => x.OutputJson).HasColumnType("mediumtext"); b.Property(x => x.DedupeKey).HasMaxLength(100);
            b.HasIndex(x => new { x.TenantId, x.InteractionId });
            b.HasIndex(x => new { x.TenantId, x.TriggerMessageId });
            b.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
            b.HasIndex(x => new { x.TenantId, x.LeadId, x.ToolName });
            b.HasIndex(x => new { x.TenantId, x.DedupeKey }).IsUnique();
            b.HasOne<AiInteraction>().WithMany().HasForeignKey(x => x.InteractionId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
