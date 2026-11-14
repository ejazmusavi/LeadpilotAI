using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Analytics;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>
/// EF mapping of the Phase 11 tables and the indexes the analytics queries need on EARLIER tables. Every index below is named after the query it serves; docs/DATABASE_DESIGN.md holds the EXPLAIN evidence.
/// </summary>
internal static class AnalyticsModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<DailyMetric>(b =>
        {
            b.ToTable("DailyMetrics");
            b.Property(x => x.MetricKey).HasMaxLength(40).IsRequired(); b.Property(x => x.Dimension).HasMaxLength(10).IsRequired();
            b.Property(x => x.Amount).HasPrecision(18, 2); b.Property(x => x.Day).HasColumnType("date"); b.Ignore(x => x.DayOnly);
            b.HasIndex(x => new { x.TenantId, x.MetricKey, x.Day, x.Dimension }).IsUnique().HasDatabaseName("UX_DailyMetrics_Tenant_Metric_Day_Dim");
        });

        builder.Entity<AnalyticsRefreshState>(b =>
        {
            b.ToTable("AnalyticsRefreshStates");
            b.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired(); b.Property(x => x.CoveredFromDay).HasColumnType("date"); b.Property(x => x.CoveredToDay).HasColumnType("date"); b.Ignore(x => x.FromDay); b.Ignore(x => x.ToDay);
            b.HasIndex(x => x.TenantId).IsUnique();
        });

        // Indexes on earlier tables (event time first after the tenant, so a date range is one index range scan). Only indexes that the benchmark showed to help are kept; docs/benchmarks/phase11-large-dataset.md lists the ones that were tried and dropped.
        builder.Entity<Message>(b =>
        {
            b.HasIndex(x => new { x.TenantId, x.Direction, x.SentAtUtc, x.DeliveredAtUtc }).HasDatabaseName("IX_Messages_Tenant_Dir_Sent");   // covers "sent" and "delivered" without row look-ups
            b.HasIndex(x => new { x.TenantId, x.ContactId, x.Direction, x.SentAtUtc }).HasDatabaseName("IX_Messages_Tenant_Contact_Dir_Sent");   // "was this lead messaged before?"
            b.HasIndex(x => new { x.TenantId, x.Direction, x.OccurredAtUtc }).HasDatabaseName("IX_Messages_Tenant_Dir_Occurred");
            b.HasIndex(x => new { x.TenantId, x.FailedAtUtc }).HasDatabaseName("IX_Messages_Tenant_Failed");
        });
        builder.Entity<Demo>(b =>
        {
            b.HasIndex(x => new { x.TenantId, x.CreatedAtUtc }).HasDatabaseName("IX_Demos_Tenant_Created");
            b.HasIndex(x => new { x.TenantId, x.CompletedAtUtc }).HasDatabaseName("IX_Demos_Tenant_Completed");
        });
        builder.Entity<Trial>().HasIndex(x => new { x.TenantId, x.CreatedAtUtc }).HasDatabaseName("IX_Trials_Tenant_Created");
        builder.Entity<Customer>().HasIndex(x => new { x.TenantId, x.ConvertedAtUtc }).HasDatabaseName("IX_Customers_Tenant_Converted");
        builder.Entity<Payment>().HasIndex(x => new { x.TenantId, x.PaidAtUtc }).HasDatabaseName("IX_Payments_Tenant_Paid");
        builder.Entity<Opportunity>().HasIndex(x => new { x.TenantId, x.Status, x.UpdatedAtUtc }).HasDatabaseName("IX_Opportunities_Tenant_Status_Updated");
        builder.Entity<CampaignRecipient>().HasIndex(x => new { x.TenantId, x.SentAtUtc, x.RepliedAtUtc }).HasDatabaseName("IX_CampaignRecipients_Tenant_Sent");
    }
}
