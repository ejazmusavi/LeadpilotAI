using LeadPilot.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>EF mapping of the Phase 12 tables. Money is decimal(18,2) with an explicit 3-letter currency column next to it.</summary>
internal static class BillingModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<Plan>(b =>
        {
            b.ToTable("Plans");
            b.Property(x => x.Code).HasMaxLength(40).IsRequired(); b.Property(x => x.Name).HasMaxLength(100).IsRequired(); b.Property(x => x.Description).HasMaxLength(1000);
            b.Property(x => x.Price).HasPrecision(18, 2); b.Property(x => x.Currency).HasMaxLength(3).IsRequired(); b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.Ignore(x => x.IsFree);
            b.HasIndex(x => x.Code).IsUnique();
            b.HasMany(x => x.Limits).WithOne().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.Features).WithOne().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PlanLimit>(b => { b.ToTable("PlanLimits"); b.HasIndex(x => new { x.PlanId, x.Dimension }).IsUnique(); });
        builder.Entity<PlanFeature>(b => { b.ToTable("PlanFeatures"); b.Property(x => x.Feature).HasMaxLength(40).IsRequired(); b.HasIndex(x => new { x.PlanId, x.Feature }).IsUnique(); });
        builder.Entity<PlatformSetting>(b => { b.ToTable("PlatformSettings"); b.Property(x => x.Key).HasMaxLength(80).IsRequired(); b.Property(x => x.Value).HasMaxLength(4000).IsRequired(); b.Property(x => x.UpdatedBy).HasMaxLength(256); b.HasIndex(x => x.Key).IsUnique(); });
        builder.Entity<InvoiceSequence>(b => { b.ToTable("InvoiceSequences"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.HasData(new InvoiceSequence { Id = 1, LastNumber = 0 }); });
        builder.Entity<RegistrationAttempt>(b =>
        {
            b.ToTable("RegistrationAttempts");
            b.Property(x => x.IpHash).HasMaxLength(64).IsRequired(); b.Property(x => x.EmailHash).HasMaxLength(64).IsRequired(); b.Property(x => x.EmailDomain).HasMaxLength(100).IsRequired(); b.Property(x => x.Reason).HasMaxLength(200);
            b.HasIndex(x => new { x.IpHash, x.CreatedAtUtc }); b.HasIndex(x => new { x.EmailDomain, x.CreatedAtUtc });
        });

        builder.Entity<TenantSubscription>(b =>
        {
            b.ToTable("TenantSubscriptions");
            b.Property(x => x.GrantReason).HasMaxLength(300); b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PendingPlanId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId).IsUnique();                       // one subscription per tenant (also the row the usage guard locks)
            b.HasIndex(x => new { x.State, x.PeriodEndUtc });              // the renewal sweep
            b.HasIndex(x => x.PlanId);
        });
        builder.Entity<SubscriptionEvent>(b =>
        {
            b.ToTable("SubscriptionEvents"); b.Property(x => x.Type).HasMaxLength(40).IsRequired(); b.Property(x => x.Summary).HasMaxLength(500).IsRequired(); b.Property(x => x.Actor).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
        });
        builder.Entity<TenantEntitlementOverride>(b =>
        {
            b.ToTable("TenantEntitlementOverrides"); b.Property(x => x.Key).HasMaxLength(40).IsRequired(); b.Property(x => x.Reason).HasMaxLength(300).IsRequired(); b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        });
        builder.Entity<UsageNotice>(b =>
        {
            b.ToTable("UsageNotices"); b.HasIndex(x => new { x.TenantId, x.Dimension, x.PeriodStartUtc, x.Threshold }).IsUnique().HasDatabaseName("UX_UsageNotices_Tenant_Dim_Period_Threshold");
        });
        builder.Entity<UsageReservation>(b => { b.ToTable("UsageReservations"); b.HasIndex(x => new { x.TenantId, x.Dimension, x.ExpiresAtUtc }); });

        builder.Entity<Invoice>(b =>
        {
            b.ToTable("Invoices");
            b.Property(x => x.Number).HasMaxLength(40); b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.Property(x => x.Subtotal).HasPrecision(18, 2); b.Property(x => x.TaxAmount).HasPrecision(18, 2); b.Property(x => x.Total).HasPrecision(18, 2); b.Property(x => x.TaxRatePercent).HasPrecision(5, 2);
            b.Property(x => x.TaxName).HasMaxLength(40); b.Property(x => x.VoidReason).HasMaxLength(300); b.Property(x => x.IdempotencyKey).HasMaxLength(80);
            b.Property(x => x.BillToName).HasMaxLength(200).IsRequired(); b.Property(x => x.SellerText).HasMaxLength(1000); b.Property(x => x.PaymentInstructions).HasMaxLength(1500); b.Property(x => x.Notes).HasMaxLength(500);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.Number).IsUnique().HasDatabaseName("UX_Invoices_Number");                                   // one platform-wide sequence: a number is never reused (NULLs = drafts)
            b.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasDatabaseName("UX_Invoices_Tenant_Key");  // the renewal job can run any number of times
            b.HasIndex(x => new { x.TenantId, x.Status, x.IssuedAtUtc });
            b.HasIndex(x => new { x.Status, x.IssuedAtUtc });
        });
        builder.Entity<InvoiceLine>(b =>
        {
            b.ToTable("InvoiceLines"); b.Property(x => x.Description).HasMaxLength(300).IsRequired();
            b.Property(x => x.Quantity).HasPrecision(18, 4); b.Property(x => x.UnitPrice).HasPrecision(18, 2); b.Property(x => x.Amount).HasPrecision(18, 2);
            b.HasIndex(x => new { x.InvoiceId, x.Position }).IsUnique();
        });
        builder.Entity<BillingPayment>(b =>
        {
            b.ToTable("BillingPayments");
            b.Property(x => x.Amount).HasPrecision(18, 2); b.Property(x => x.Currency).HasMaxLength(3).IsRequired(); b.Property(x => x.EvidenceReference).HasMaxLength(150).IsRequired(); b.Property(x => x.EvidenceKey).HasMaxLength(150).IsRequired(); b.Property(x => x.Notes).HasMaxLength(500);
            b.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.InvoiceId).IsUnique();                         // one verified payment per invoice: no partial or double payment
            b.HasIndex(x => x.EvidenceKey).IsUnique().HasDatabaseName("UX_BillingPayments_EvidenceKey");   // one receipt proves one payment, platform-wide
            b.HasIndex(x => new { x.TenantId, x.PaidAtUtc });
        });
        builder.Entity<PaymentReport>(b =>
        {
            b.ToTable("PaymentReports"); b.Property(x => x.EvidenceReference).HasMaxLength(150).IsRequired(); b.Property(x => x.Note).HasMaxLength(500); b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.InvoiceId }); b.HasIndex(x => new { x.Status, x.CreatedAtUtc });
        });
        builder.Entity<PlanChangeRequest>(b =>
        {
            b.ToTable("PlanChangeRequests");
            b.Property(x => x.CreditAmount).HasPrecision(18, 2); b.Property(x => x.Currency).HasMaxLength(3); b.Property(x => x.CloseReason).HasMaxLength(300); b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.HasOne<Plan>().WithMany().HasForeignKey(x => x.FromPlanId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Plan>().WithMany().HasForeignKey(x => x.ToPlanId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.IsOpen });
        });
    }
}
