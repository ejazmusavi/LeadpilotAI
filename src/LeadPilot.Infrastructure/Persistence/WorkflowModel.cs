using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>EF mapping of the Phase 9 workflow tables. Tenant ownership (FK, index, query filter) is applied afterwards for every ITenantOwned entity.</summary>
internal static class WorkflowModel
{
    private static PropertyBuilder<TEnum> Str<TEnum>(this PropertyBuilder<TEnum> p, int max = 30) where TEnum : struct, Enum => p.HasConversion<string>().HasMaxLength(max);
    private static PropertyBuilder<TEnum?> StrN<TEnum>(this PropertyBuilder<TEnum?> p, int max = 30) where TEnum : struct, Enum => p.HasConversion<string>().HasMaxLength(max);

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<Workflow>(b =>
        {
            b.ToTable("Workflows");
            b.Property(x => x.Name).HasMaxLength(150).IsRequired(); b.Property(x => x.Description).HasMaxLength(1000);
            b.Property(x => x.Status).Str(); b.Property(x => x.KillReason).HasMaxLength(300);
            b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.IsDeleted, x.Status });
            b.HasIndex(x => new { x.TenantId, x.Name });
        });

        builder.Entity<WorkflowVersion>(b =>
        {
            b.ToTable("WorkflowVersions");
            b.Property(x => x.State).Str(); b.Property(x => x.ReEntry).Str(); b.Property(x => x.Note).HasMaxLength(300); b.Property(x => x.CreatedBy).HasMaxLength(256);
            b.HasIndex(x => new { x.TenantId, x.WorkflowId, x.Number }).IsUnique();
            b.HasOne<Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<WorkflowTrigger>(b =>
        {
            b.ToTable("WorkflowTriggers");
            b.Property(x => x.EventType).HasMaxLength(60).IsRequired(); b.Property(x => x.ConditionJson).HasColumnType("text"); b.Property(x => x.ConfigJson).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.EventType });
            b.HasIndex(x => new { x.WorkflowVersionId, x.EventType }).IsUnique();
            b.HasOne<WorkflowVersion>().WithMany().HasForeignKey(x => x.WorkflowVersionId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<WorkflowStep>(b =>
        {
            b.ToTable("WorkflowSteps");
            b.Property(x => x.Key).HasMaxLength(20).IsRequired(); b.Property(x => x.Type).Str(); b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.ConfigJson).HasColumnType("text").IsRequired(); b.Property(x => x.ConditionJson).HasColumnType("text"); b.Property(x => x.OnFailure).Str(20);
            b.HasIndex(x => new { x.WorkflowVersionId, x.Key }).IsUnique();
            b.HasIndex(x => new { x.WorkflowVersionId, x.Position });
            b.HasOne<WorkflowVersion>().WithMany().HasForeignKey(x => x.WorkflowVersionId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<WorkflowExecution>(b =>
        {
            b.ToTable("WorkflowExecutions");
            b.Property(x => x.TriggerKey).HasMaxLength(60).IsRequired(); b.Property(x => x.Status).Str(20); b.Property(x => x.CurrentStepKey).HasMaxLength(20);
            b.Property(x => x.StateJson).HasColumnType("text").IsRequired(); b.Property(x => x.StatusReason).HasMaxLength(500); b.Property(x => x.StartedBy).HasMaxLength(256);
            b.Property(x => x.ActiveSlot).HasMaxLength(40);
            b.Property(x => x.Version).IsConcurrencyToken();
            // idempotency: the same event never starts the same version twice for the same lead (redelivery, concurrent consumers)
            b.HasIndex(x => new { x.WorkflowVersionId, x.TriggerEventId, x.EntityId }).IsUnique();
            // exclusivity for the Once / AfterCompletion policies: only one live run per workflow and lead (NULL once the run ended, and NULLs never collide)
            b.HasIndex(x => new { x.WorkflowId, x.EntityId, x.ActiveSlot }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.NextRunAtUtc });
            b.HasIndex(x => new { x.TenantId, x.WorkflowId, x.CreatedAtUtc });
            b.HasIndex(x => new { x.TenantId, x.EntityId });
            b.HasOne<Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<WorkflowVersion>().WithMany().HasForeignKey(x => x.WorkflowVersionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Lead>().WithMany().HasForeignKey(x => x.EntityId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<WorkflowExecutionLog>(b =>
        {
            b.ToTable("WorkflowExecutionLogs");
            b.Property(x => x.StepKey).HasMaxLength(20); b.Property(x => x.StepName).HasMaxLength(100); b.Property(x => x.StepType).StrN();
            b.Property(x => x.Outcome).Str(20); b.Property(x => x.Message).HasMaxLength(1000).IsRequired(); b.Property(x => x.EffectRef).HasMaxLength(100);
            b.HasIndex(x => new { x.WorkflowExecutionId, x.OccurredAtUtc });
            b.HasOne<WorkflowExecution>().WithMany().HasForeignKey(x => x.WorkflowExecutionId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<WorkflowTenantSettings>(b =>
        {
            b.ToTable("WorkflowTenantSettings");
            b.Property(x => x.KillSwitchReason).HasMaxLength(300); b.Property(x => x.CreatedBy).HasMaxLength(256); b.Property(x => x.UpdatedBy).HasMaxLength(256);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => x.TenantId).IsUnique();
        });

        builder.Entity<WorkflowNotification>(b =>
        {
            b.ToTable("WorkflowNotifications");
            b.Property(x => x.StepKey).HasMaxLength(20).IsRequired(); b.Property(x => x.Title).HasMaxLength(150).IsRequired(); b.Property(x => x.Body).HasMaxLength(1000).IsRequired();
            b.HasIndex(x => new { x.WorkflowExecutionId, x.StepKey, x.UserId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.ReadAtUtc });
            b.HasOne<WorkflowExecution>().WithMany().HasForeignKey(x => x.WorkflowExecutionId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
