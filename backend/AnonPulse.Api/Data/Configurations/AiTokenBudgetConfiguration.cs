using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class AiTokenBudgetPeriodConfiguration : IEntityTypeConfiguration<AiTokenBudgetPeriod>
{
    public void Configure(EntityTypeBuilder<AiTokenBudgetPeriod> builder)
    {
        builder.ToTable("ai_token_budget_periods", table =>
        {
            table.HasCheckConstraint("ck_ai_token_budget_period_limit", "token_limit > 0");
            table.HasCheckConstraint("ck_ai_token_budget_period_counts",
                "reserved_tokens >= 0 AND estimated_consumed_tokens >= 0 AND actual_tokens >= 0");
            table.HasCheckConstraint("ck_ai_token_budget_period_dates", "period_end > period_start");
        });
        builder.HasKey(item => item.PeriodStart);
        builder.Property(item => item.PeriodStart).HasColumnName("period_start")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.PeriodEnd).HasColumnName("period_end")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.TokenLimit).HasColumnName("token_limit");
        builder.Property(item => item.ReservedTokens).HasColumnName("reserved_tokens");
        builder.Property(item => item.EstimatedConsumedTokens)
            .HasColumnName("estimated_consumed_tokens");
        builder.Property(item => item.ActualTokens).HasColumnName("actual_tokens");
    }
}

public sealed class AiTokenBudgetReservationConfiguration
    : IEntityTypeConfiguration<AiTokenBudgetReservation>
{
    public void Configure(EntityTypeBuilder<AiTokenBudgetReservation> builder)
    {
        builder.ToTable("ai_token_budget_reservations", table =>
        {
            table.HasCheckConstraint("ck_ai_token_budget_reservation_status",
                "status IN ('reserved', 'settled', 'released')");
            table.HasCheckConstraint("ck_ai_token_budget_reservation_counts",
                "reserved_tokens > 0 AND charged_tokens >= 0");
            table.HasCheckConstraint("ck_ai_token_budget_reservation_attempt",
                "attempt_number > 0");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.PeriodStart).HasColumnName("period_start")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.WorkloadType).HasColumnName("workload_type")
            .HasMaxLength(32);
        builder.Property(item => item.WorkloadId).HasColumnName("workload_id");
        builder.Property(item => item.AttemptNumber).HasColumnName("attempt_number");
        builder.Property(item => item.ReservedTokens).HasColumnName("reserved_tokens");
        builder.Property(item => item.ActualPromptTokens).HasColumnName("actual_prompt_tokens");
        builder.Property(item => item.ActualCompletionTokens)
            .HasColumnName("actual_completion_tokens");
        builder.Property(item => item.ChargedTokens).HasColumnName("charged_tokens");
        builder.Property(item => item.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(item => item.CreatedAt).HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.SettledAt).HasColumnName("settled_at")
            .HasColumnType("timestamp with time zone");
        builder.HasIndex(item => new { item.WorkloadType, item.WorkloadId, item.AttemptNumber })
            .IsUnique().HasDatabaseName("ux_ai_token_budget_workload_attempt");
        builder.HasOne(item => item.Period).WithMany(period => period.Reservations)
            .HasForeignKey(item => item.PeriodStart).OnDelete(DeleteBehavior.Cascade);
    }
}
