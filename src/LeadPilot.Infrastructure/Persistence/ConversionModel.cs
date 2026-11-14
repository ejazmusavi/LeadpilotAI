using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>EF mapping of the Phase 10 tables (demos, trials, customers, payments). Tenant ownership (FK, index, query filter) is applied afterwards for every ITenantOwned entity.</summary>
internal static class ConversionModel
{
    private static PropertyBuilder<TEnum> Str<TEnum>(this PropertyBuilder<TEnum> p, int max = 30) where TEnum : struct, Enum => p.HasConversion<string>().HasMaxLength(max);
    private static PropertyBuilder<TEnum?> StrN<TEnum>(this PropertyBuilder<TEnum?> p, int max = 30) where TEnum : struct, Enum => p.HasConversion<string>().HasMaxLength(max);
    private static PropertyBuilder<decimal> Money(this PropertyBuilder<decimal> p) => p.HasPrecision(18, 2);
    private static PropertyBuilder<decimal?> MoneyN(this PropertyBuilder<decimal?> p) => p.HasPrecision(18, 2);

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<ConversionSettings>(b =>
        {
            b.ToTable("ConversionSettings");
            b.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired(); b.Property(x => x.Assignment).Str(20);
            b.Property(x => x.MeetingLinkTemplate).HasMaxLength(300); b.Property(x => x.ReminderOffsets).HasMaxLength(60).IsRequired();
            b.Property(x => x.ReminderBody).HasMaxLength(600).IsRequired(); b.Property(x => x.TrialNoticeBody).HasMaxLength(600).IsRequired(); b.Property(x => x.PaymentInstructions).HasMaxLength(1500);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => x.TenantId).IsUnique();
        });

        builder.Entity<DemoRep>(b =>
        {
            b.ToTable("DemoReps");
            b.Property(x => x.TimeZoneId).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            b.HasOne<LeadPilot.Infrastructure.Identity.ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DemoAvailabilityRule>(b =>
        {
            b.ToTable("DemoAvailabilityRules");
            b.Property(x => x.Day).HasConversion<int>();
            b.HasIndex(x => new { x.TenantId, x.RepId, x.Day });
            b.HasOne<DemoRep>().WithMany().HasForeignKey(x => x.RepId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DemoAvailabilityException>(b =>
        {
            b.ToTable("DemoAvailabilityExceptions");
            b.Property(x => x.Kind).Str(); b.Property(x => x.Note).HasMaxLength(200); b.Property(x => x.Date).HasColumnType("date");
            b.HasIndex(x => new { x.TenantId, x.RepId, x.Date });
            b.HasOne<DemoRep>().WithMany().HasForeignKey(x => x.RepId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Demo>(b =>
        {
            b.ToTable("Demos");
            b.Property(x => x.Status).Str(); b.Property(x => x.Source).Str(); b.Property(x => x.MeetingLink).HasMaxLength(500); b.Property(x => x.Notes).HasMaxLength(500);
            b.Property(x => x.CancelReason).HasMaxLength(300); b.Property(x => x.ActiveSlot).HasMaxLength(60);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            // THE double-booking guard: one active demo per rep and start instant (NULL once the demo no longer holds the slot; NULLs never collide)
            b.HasIndex(x => new { x.TenantId, x.ActiveSlot }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.RepUserId, x.StartUtc });
            b.HasIndex(x => new { x.TenantId, x.Status, x.StartUtc });
            b.HasIndex(x => new { x.TenantId, x.LeadId });
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LeadPilot.Infrastructure.Identity.ApplicationUser>().WithMany().HasForeignKey(x => x.RepUserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DemoHistory>(b =>
        {
            b.ToTable("DemoHistory");
            b.Property(x => x.Type).HasMaxLength(40).IsRequired(); b.Property(x => x.Summary).HasMaxLength(500).IsRequired(); b.Property(x => x.ActorKind).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.DemoId, x.OccurredAtUtc });
            b.HasOne<Demo>().WithMany().HasForeignKey(x => x.DemoId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DemoReminder>(b =>
        {
            b.ToTable("DemoReminders");
            b.Property(x => x.Status).Str(); b.Property(x => x.Reason).HasMaxLength(300);
            b.HasIndex(x => new { x.DemoId, x.Generation, x.OffsetMinutes }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.DueAtUtc });
            b.HasOne<Demo>().WithMany().HasForeignKey(x => x.DemoId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Trial>(b =>
        {
            b.ToTable("Trials");
            b.Property(x => x.PlanName).HasMaxLength(100); b.Property(x => x.ProductName).HasMaxLength(100); b.Property(x => x.Status).Str(); b.Property(x => x.Source).Str();
            b.Property(x => x.ProvisioningNote).HasMaxLength(500); b.Property(x => x.ExternalAccountRef).HasMaxLength(150); b.Property(x => x.LoginUrl).HasMaxLength(500);
            b.Property(x => x.UsageNotes).HasMaxLength(500); b.Property(x => x.EndReason).HasMaxLength(300); b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Status, x.ExpiresAtUtc });
            b.HasIndex(x => new { x.TenantId, x.LeadId });
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LeadPilot.Domain.Agents.PricingPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<TrialEvent>(b =>
        {
            b.ToTable("TrialEvents");
            b.Property(x => x.Type).HasMaxLength(40).IsRequired(); b.Property(x => x.Summary).HasMaxLength(500).IsRequired(); b.Property(x => x.ActorKind).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.TrialId, x.OccurredAtUtc });
            b.HasOne<Trial>().WithMany().HasForeignKey(x => x.TrialId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Customer>(b =>
        {
            b.ToTable("Customers");
            b.Property(x => x.Name).HasMaxLength(200).IsRequired(); b.Property(x => x.Status).Str(20); b.Property(x => x.AccountReference).HasMaxLength(150); b.Property(x => x.AccountNotes).HasMaxLength(500);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256); b.Ignore(x => x.Code);
            b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LeadId }).IsUnique();          // one customer record per lead; further deals attach to it
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Contact>().WithMany().HasForeignKey(x => x.PrimaryContactId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Subscription>(b =>
        {
            b.ToTable("Subscriptions", t => t.HasCheckConstraint("CK_Subscriptions_ActiveNeedsPayment", "`Status` <> 'Active' OR `ActivatedByPaymentId` IS NOT NULL"));
            b.Property(x => x.PlanName).HasMaxLength(100).IsRequired(); b.Property(x => x.ProductName).HasMaxLength(100); b.Property(x => x.Status).Str(20); b.Property(x => x.BillingCycle).Str(20);
            b.Property(x => x.CancelReason).HasMaxLength(300); b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.CustomerId, x.Status });
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LeadPilot.Domain.Agents.PricingPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Payment>().WithMany().HasForeignKey(x => x.ActivatedByPaymentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CommercialAgreement>(b =>
        {
            b.ToTable("CommercialAgreements");
            b.Property(x => x.Terms).HasColumnType("text").IsRequired(); b.Property(x => x.ListPrice).MoneyN(); b.Property(x => x.Price).Money(); b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.Property(x => x.BillingCycle).Str(20); b.Property(x => x.DiscountPercent).HasPrecision(5, 2);
            b.HasIndex(x => new { x.TenantId, x.OpportunityId }).IsUnique();   // a won opportunity is converted exactly once
            b.HasIndex(x => x.SubscriptionId).IsUnique();
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LeadPilot.Infrastructure.Identity.ApplicationUser>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PaymentRequest>(b =>
        {
            b.ToTable("PaymentRequests");
            b.Property(x => x.Reference).HasMaxLength(40).IsRequired(); b.Property(x => x.Amount).Money(); b.Property(x => x.Currency).HasMaxLength(3).IsRequired(); b.Property(x => x.Status).Str(20);
            b.Property(x => x.Instructions).HasMaxLength(1500); b.Property(x => x.PayUrl).HasMaxLength(500); b.Property(x => x.RequestedVia).HasMaxLength(20).IsRequired(); b.Property(x => x.CloseReason).HasMaxLength(300);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Reference }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.OpenForSubscription }).IsUnique();   // at most one OPEN request per subscription
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAtUtc });
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CommercialAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Payment>(b =>
        {
            b.ToTable("Payments");
            b.Property(x => x.Amount).Money(); b.Property(x => x.Currency).HasMaxLength(3).IsRequired(); b.Property(x => x.Method).Str(20); b.Property(x => x.Source).Str(20);
            b.Property(x => x.EvidenceReference).HasMaxLength(150).IsRequired(); b.Property(x => x.EvidenceKey).HasMaxLength(150).IsRequired(); b.Property(x => x.ProviderKey).HasMaxLength(50);
            b.Property(x => x.ProviderEventId).HasMaxLength(150); b.Property(x => x.Notes).HasMaxLength(500);
            b.HasIndex(x => x.PaymentRequestId).IsUnique();                        // a request is paid at most once
            b.HasIndex(x => new { x.TenantId, x.EvidenceKey }).IsUnique();         // the same receipt cannot verify two payments
            b.HasOne<PaymentRequest>().WithMany().HasForeignKey(x => x.PaymentRequestId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PaymentProviderConfig>(b =>
        {
            b.ToTable("PaymentProviderConfigs");
            b.Property(x => x.ProviderKey).HasMaxLength(50).IsRequired(); b.Property(x => x.SecretProtected).HasMaxLength(1000).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => x.WebhookId).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.ProviderKey }).IsUnique();
        });

        builder.Entity<PaymentEvent>(b =>
        {
            b.ToTable("PaymentEvents");
            b.Property(x => x.ProviderKey).HasMaxLength(50).IsRequired(); b.Property(x => x.ExternalEventId).HasMaxLength(150).IsRequired(); b.Property(x => x.Outcome).Str(20);
            b.Property(x => x.Reason).HasMaxLength(300); b.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.ProviderKey, x.ExternalEventId }).IsUnique();   // idempotency of provider deliveries
        });
    }
}
