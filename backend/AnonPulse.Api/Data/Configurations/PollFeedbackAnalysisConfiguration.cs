using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AnonPulse.Api.Data.Configurations;

public sealed class PollFeedbackAnalysisConfiguration
    : IEntityTypeConfiguration<PollFeedbackAnalysis>
{
    public void Configure(EntityTypeBuilder<PollFeedbackAnalysis> builder)
    {
        builder.ToTable("poll_feedback_analyses", table =>
        {
            table.HasCheckConstraint("ck_poll_feedback_analyses_status",
                "status IN ('pending', 'processing', 'completed', 'failed')");
            table.HasCheckConstraint("ck_poll_feedback_analyses_attempts", "attempts >= 0");
            table.HasCheckConstraint("ck_poll_feedback_analyses_answer_count", "answer_count > 0");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.PollId).HasColumnName("poll_id");
        builder.Property(item => item.QuestionId).HasColumnName("question_id");
        builder.Property(item => item.Status).HasColumnName("status").HasMaxLength(16)
            .HasConversion(new ValueConverter<FeedbackAnalysisStatus, string>(
                value => value.ToString().ToLowerInvariant(),
                value => Enum.Parse<FeedbackAnalysisStatus>(value, true)));
        builder.Property(item => item.Provider).HasColumnName("provider").HasMaxLength(32);
        builder.Property(item => item.Model).HasColumnName("model").HasMaxLength(128);
        builder.Property(item => item.AlgorithmVersion).HasColumnName("algorithm_version").HasMaxLength(32);
        builder.Property(item => item.AnswerCount).HasColumnName("answer_count");
        builder.Property(item => item.AnswerFingerprint).HasColumnName("answer_fingerprint").HasMaxLength(64);
        builder.Property(item => item.ReportJson).HasColumnName("report").HasColumnType("jsonb");
        builder.Property(item => item.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.StartedAt).HasColumnName("started_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.Attempts).HasColumnName("attempts");
        builder.Property(item => item.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
        builder.Property(item => item.PromptTokens).HasColumnName("prompt_tokens");
        builder.Property(item => item.CompletionTokens).HasColumnName("completion_tokens");
        builder.Property(item => item.LeaseId).HasColumnName("lease_id");
        builder.Property(item => item.LeaseGeneration).HasColumnName("lease_generation");
        builder.HasIndex(item => new { item.QuestionId, item.AnswerFingerprint })
            .IsUnique().HasDatabaseName("ux_feedback_analyses_question_fingerprint");
        builder.HasIndex(item => new { item.Status, item.NextAttemptAt, item.CreatedAt })
            .HasDatabaseName("ix_feedback_analyses_queue");
        builder.HasOne(item => item.Poll).WithMany(poll => poll.FeedbackAnalyses)
            .HasForeignKey(item => item.PollId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Question).WithMany(question => question.FeedbackAnalyses)
            .HasForeignKey(item => item.QuestionId).OnDelete(DeleteBehavior.Cascade);
    }
}
