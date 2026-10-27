using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AnonPulse.Api.Data.Configurations;

public sealed class PollKnowledgeGradingJobConfiguration
    : IEntityTypeConfiguration<PollKnowledgeGradingJob>
{
    public void Configure(EntityTypeBuilder<PollKnowledgeGradingJob> builder)
    {
        builder.ToTable("poll_knowledge_grading_jobs", table =>
        {
            table.HasCheckConstraint("ck_knowledge_grading_jobs_status",
                "status IN ('pending', 'processing', 'completed', 'failed')");
            table.HasCheckConstraint("ck_knowledge_grading_jobs_counts",
                "answer_count > 0 AND suggested_count >= 0 AND suggested_count <= answer_count");
            table.HasCheckConstraint("ck_knowledge_grading_jobs_score", "max_score_snapshot BETWEEN 1 AND 100");
            table.HasCheckConstraint("ck_knowledge_grading_jobs_attempts",
                "automatic_attempts >= 0 AND manual_retry_number >= 0 AND lease_generation >= 0");
            table.HasCheckConstraint("ck_knowledge_grading_jobs_budget",
                "reserved_input_tokens > 0 AND reserved_output_tokens > 0");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.PollId).HasColumnName("poll_id");
        builder.Property(item => item.QuestionId).HasColumnName("question_id");
        builder.Property(item => item.Status).HasColumnName("status").HasMaxLength(16)
            .HasConversion(new ValueConverter<KnowledgeGradingJobStatus, string>(
                value => value.ToString().ToLowerInvariant(),
                value => Enum.Parse<KnowledgeGradingJobStatus>(value, true)));
        builder.Property(item => item.Provider).HasColumnName("provider").HasMaxLength(32);
        builder.Property(item => item.Model).HasColumnName("model").HasMaxLength(128);
        builder.Property(item => item.AlgorithmVersion).HasColumnName("algorithm_version").HasMaxLength(32);
        builder.Property(item => item.SnapshotFingerprint).HasColumnName("snapshot_fingerprint").HasMaxLength(64);
        builder.Property(item => item.ExpectedAnswerSnapshot).HasColumnName("expected_answer_snapshot").HasColumnType("text");
        builder.Property(item => item.GradingCriteriaSnapshot).HasColumnName("grading_criteria_snapshot").HasColumnType("text");
        builder.Property(item => item.MaxScoreSnapshot).HasColumnName("max_score_snapshot");
        builder.Property(item => item.AnswersSnapshotJson).HasColumnName("answers_snapshot").HasColumnType("jsonb");
        builder.Property(item => item.AnswerCount).HasColumnName("answer_count");
        builder.Property(item => item.SuggestedCount).HasColumnName("suggested_count");
        builder.Property(item => item.ReservedInputTokens).HasColumnName("reserved_input_tokens");
        builder.Property(item => item.ReservedOutputTokens).HasColumnName("reserved_output_tokens");
        builder.Property(item => item.PromptTokens).HasColumnName("prompt_tokens");
        builder.Property(item => item.CompletionTokens).HasColumnName("completion_tokens");
        builder.Property(item => item.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.StartedAt).HasColumnName("started_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.AutomaticAttempts).HasColumnName("automatic_attempts");
        builder.Property(item => item.ManualRetryNumber).HasColumnName("manual_retry_number");
        builder.Property(item => item.PreviousJobId).HasColumnName("previous_job_id");
        builder.Property(item => item.LeaseId).HasColumnName("lease_id");
        builder.Property(item => item.LeaseGeneration).HasColumnName("lease_generation");
        builder.Property(item => item.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
        builder.HasIndex(item => new
        { item.QuestionId, item.SnapshotFingerprint, item.ManualRetryNumber })
            .IsUnique().HasDatabaseName("ux_knowledge_grading_question_fingerprint");
        builder.HasIndex(item => new { item.Status, item.NextAttemptAt, item.CreatedAt })
            .HasDatabaseName("ix_knowledge_grading_queue");
        builder.HasOne(item => item.Poll).WithMany(poll => poll.KnowledgeGradingJobs)
            .HasForeignKey(item => item.PollId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Question).WithMany(question => question.KnowledgeGradingJobs)
            .HasForeignKey(item => item.QuestionId).OnDelete(DeleteBehavior.Cascade);
    }
}
