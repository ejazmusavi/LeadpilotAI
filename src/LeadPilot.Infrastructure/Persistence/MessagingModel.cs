using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>EF mapping of the Phase 5 messaging tables. Tenant ownership (FK, index, query filter) is applied afterwards for every ITenantOwned entity.</summary>
internal static class MessagingModel
{
    private static PropertyBuilder<TEnum> Str<TEnum>(this PropertyBuilder<TEnum> p, int max = 30) where TEnum : struct, Enum => p.HasConversion<string>().HasMaxLength(max);

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<Channel>(b =>
        {
            b.ToTable("Channels");
            b.Property(x => x.Type).Str(20); b.Property(x => x.Provider).Str(20);
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Type, x.Provider }).IsUnique();
        });

        builder.Entity<ChannelAccount>(b =>
        {
            b.ToTable("ChannelAccounts");
            b.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            b.Property(x => x.GatewayAccountId).HasMaxLength(63).IsRequired();
            b.Property(x => x.WebhookKey).HasMaxLength(64).IsRequired();
            b.Property(x => x.ConnectionStatus).Str(20);
            b.Property(x => x.PhoneE164).HasMaxLength(20); b.Property(x => x.PushName).HasMaxLength(200); b.Property(x => x.Engine).HasMaxLength(30);
            b.Property(x => x.PauseReason).HasMaxLength(300);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            // The webhook→tenant resolution relies on both being unique across ALL tenants.
            b.HasIndex(x => x.GatewayAccountId).IsUnique();
            b.HasIndex(x => x.WebhookKey).IsUnique();
            b.HasOne<Channel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ChannelCredential>(b =>
        {
            b.ToTable("ChannelCredentials");
            b.Property(x => x.ProtectedBaseUrl).HasMaxLength(2000).IsRequired();
            b.Property(x => x.ProtectedApiKey).HasMaxLength(2000).IsRequired();
            b.Property(x => x.ProtectedWebhookSecret).HasMaxLength(2000).IsRequired();
            b.Property(x => x.KeyHint).HasMaxLength(20);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => x.ChannelAccountId).IsUnique();
            b.HasOne<ChannelAccount>().WithMany().HasForeignKey(x => x.ChannelAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Conversation>(b =>
        {
            b.ToTable("Conversations");
            b.Property(x => x.ExternalChatId).HasMaxLength(100).IsRequired();
            b.Property(x => x.PeerPhoneE164).HasMaxLength(20); b.Property(x => x.PeerName).HasMaxLength(200);
            b.Property(x => x.Status).Str(20); b.Property(x => x.AiMode).Str(20);
            b.Property(x => x.LastMessagePreview).HasMaxLength(200);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.ChannelAccountId, x.ExternalChatId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LastMessageAtUtc });
            b.HasIndex(x => new { x.TenantId, x.ContactId });
            b.HasOne<ChannelAccount>().WithMany().HasForeignKey(x => x.ChannelAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ConversationAssignment>(b =>
        {
            b.ToTable("ConversationAssignments");
            b.HasIndex(x => new { x.TenantId, x.ConversationId, x.AssignedAtUtc });
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Message>(b =>
        {
            b.ToTable("Messages");
            b.Property(x => x.Direction).Str(20); b.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(20); b.Property(x => x.Status).Str(20);
            b.Property(x => x.ExternalMessageId).HasMaxLength(200);
            b.Property(x => x.Body).HasColumnType("text").IsRequired();
            b.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            b.Property(x => x.IdempotencyKey).HasMaxLength(100);
            b.Property(x => x.FailureCode).HasMaxLength(50); b.Property(x => x.FailureMessage).HasMaxLength(500);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.ConversationId, x.OccurredAtUtc });
            // Exactly-once storage of gateway messages, and exactly-one message per outbound idempotency key (NULLs never collide in MySQL).
            b.HasIndex(x => new { x.TenantId, x.ChannelAccountId, x.ExternalMessageId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            b.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
            b.HasIndex(x => new { x.TenantId, x.ChannelAccountId, x.SentAtUtc });
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ChannelAccount>().WithMany().HasForeignKey(x => x.ChannelAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<MessageTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MessageAttachment>(b =>
        {
            b.ToTable("MessageAttachments");
            b.Property(x => x.FileName).HasMaxLength(200); b.Property(x => x.MimeType).HasMaxLength(100).IsRequired();
            b.Property(x => x.Status).Str(20); b.Property(x => x.RejectionReason).HasMaxLength(200); b.Property(x => x.StorageKey).HasMaxLength(300);
            b.HasIndex(x => new { x.TenantId, x.MessageId });
            b.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MessageStatusEvent>(b =>
        {
            b.ToTable("MessageStatusEvents");
            b.Property(x => x.Status).Str(20); b.Property(x => x.Source).HasMaxLength(30).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.MessageId, x.RecordedAtUtc });
            b.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<WebhookEvent>(b =>
        {
            b.ToTable("WebhookEvents");
            b.Property(x => x.ExternalEventId).HasMaxLength(128).IsRequired();
            b.Property(x => x.EventType).HasMaxLength(60).IsRequired();
            b.Property(x => x.PayloadJson).HasColumnType("longtext").IsRequired();
            b.Property(x => x.Status).Str(20); b.Property(x => x.Note).HasMaxLength(500);
            b.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired(); b.Property(x => x.SourceIp).HasMaxLength(64);
            b.HasIndex(x => new { x.ChannelAccountId, x.ExternalEventId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.ReceivedAtUtc });
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.HasIndex(x => x.ExpiresAtUtc);
            b.HasOne<ChannelAccount>().WithMany().HasForeignKey(x => x.ChannelAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MessageTemplate>(b =>
        {
            b.ToTable("MessageTemplates");
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Body).HasColumnType("text").IsRequired();
            b.Property(x => x.Category).Str(20);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        });

        builder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("OutboxMessages");
            b.Property(x => x.Type).HasMaxLength(60).IsRequired();
            b.Property(x => x.PayloadJson).HasColumnType("text").IsRequired();
            b.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired(); b.Property(x => x.CausationId).HasMaxLength(64);
            b.Property(x => x.Status).Str(20); b.Property(x => x.LastError).HasMaxLength(500);
            b.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
            b.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        });

        builder.Entity<ProcessedMessage>(b =>
        {
            b.ToTable("ProcessedMessages");
            b.Property(x => x.ConsumerName).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.ConsumerName, x.MessageId }).IsUnique();
        });

        builder.Entity<ChannelAccountNotification>(b =>
        {
            b.ToTable("ChannelAccountNotifications");
            b.Property(x => x.Kind).Str(30); b.Property(x => x.Message).HasMaxLength(500).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.ChannelAccountId, x.CreatedAtUtc });
            b.HasOne<ChannelAccount>().WithMany().HasForeignKey(x => x.ChannelAccountId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
