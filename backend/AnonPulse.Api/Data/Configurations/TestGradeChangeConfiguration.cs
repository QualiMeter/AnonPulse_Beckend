using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class TestGradeChangeConfiguration : IEntityTypeConfiguration<TestGradeChange>
{
    public void Configure(EntityTypeBuilder<TestGradeChange> builder)
    {
        builder.ToTable("test_grade_changes", table =>
        {
            table.HasCheckConstraint("ck_test_grade_changes_scores",
                "(old_score IS NULL OR old_score >= 0) AND new_score >= 0");
            table.HasCheckConstraint("ck_test_grade_changes_source",
                "source IN ('manual', 'ai_suggestion') AND " +
                "((source = 'manual' AND knowledge_grading_suggestion_id IS NULL) OR " +
                "(source = 'ai_suggestion' AND knowledge_grading_suggestion_id IS NOT NULL))");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.AttemptId).HasColumnName("attempt_id");
        builder.Property(item => item.AnswerId).HasColumnName("answer_id");
        builder.Property(item => item.ActorId).HasColumnName("actor_id");
        builder.Property(item => item.OldScore).HasColumnName("old_score")
            .HasColumnType("numeric(10,2)");
        builder.Property(item => item.NewScore).HasColumnName("new_score")
            .HasColumnType("numeric(10,2)");
        builder.Property(item => item.Source).HasColumnName("source").HasMaxLength(16);
        builder.Property(item => item.KnowledgeGradingSuggestionId)
            .HasColumnName("knowledge_grading_suggestion_id");
        builder.Property(item => item.ChangedAt).HasColumnName("changed_at")
            .HasColumnType("timestamp with time zone");
        builder.HasIndex(item => new { item.AttemptId, item.ChangedAt })
            .HasDatabaseName("ix_test_grade_changes_attempt_changed_at");
        builder.HasIndex(item => item.AnswerId)
            .HasDatabaseName("ix_test_grade_changes_answer_id");
        builder.HasIndex(item => item.KnowledgeGradingSuggestionId)
            .HasDatabaseName("ix_test_grade_changes_suggestion_id");
        builder.HasOne(item => item.Attempt).WithMany(attempt => attempt.GradeChanges)
            .HasForeignKey(item => item.AttemptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Answer).WithMany()
            .HasForeignKey(item => new { item.AttemptId, item.AnswerId })
            .HasPrincipalKey(answer => new { answer.AttemptId, answer.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.KnowledgeGradingSuggestion).WithMany()
            .HasForeignKey(item => item.KnowledgeGradingSuggestionId)
            .OnDelete(DeleteBehavior.Restrict);
        // ActorId intentionally has no user FK so audit survives account deletion.
    }
}
