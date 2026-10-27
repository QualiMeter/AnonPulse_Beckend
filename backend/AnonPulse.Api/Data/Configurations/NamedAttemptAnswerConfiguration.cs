using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class NamedAttemptAnswerConfiguration : IEntityTypeConfiguration<NamedAttemptAnswer>
{
    public void Configure(EntityTypeBuilder<NamedAttemptAnswer> builder)
    {
        builder.ToTable("named_attempt_answers", table =>
        {
            table.HasCheckConstraint("ck_named_attempt_answers_grading_state",
                "grading_state IN ('auto_graded', 'pending', 'teacher_graded')");
            table.HasCheckConstraint("ck_named_attempt_answers_score",
                "maximum_score > 0 AND ((grading_state IN ('auto_graded', 'teacher_graded') AND score IS NOT NULL AND score >= 0 AND score <= maximum_score) OR " +
                "(grading_state = 'pending' AND score IS NULL))");
            table.HasCheckConstraint("ck_named_attempt_answers_text", "text IS NULL OR btrim(text) <> ''");
        });
        builder.HasKey(item => item.Id);
        builder.HasAlternateKey(item => new { item.AttemptId, item.Id })
            .HasName("ak_named_attempt_answers_attempt_id_id");
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.AttemptId).HasColumnName("attempt_id");
        builder.Property(item => item.QuestionId).HasColumnName("question_id");
        builder.Property(item => item.Text).HasColumnName("text").HasColumnType("text");
        builder.Property(item => item.GradingState).HasColumnName("grading_state").HasMaxLength(16);
        builder.Property(item => item.Score).HasColumnName("score").HasColumnType("numeric(10,2)");
        builder.Property(item => item.MaximumScore).HasColumnName("maximum_score").HasColumnType("numeric(10,2)");
        builder.HasIndex(item => new { item.AttemptId, item.QuestionId }).IsUnique()
            .HasDatabaseName("ux_named_attempt_answers_attempt_id_question_id");
        builder.HasIndex(item => item.QuestionId);
        builder.HasOne(item => item.Attempt).WithMany(attempt => attempt.Answers)
            .HasForeignKey(item => item.AttemptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Question).WithMany()
            .HasForeignKey(item => item.QuestionId).OnDelete(DeleteBehavior.Restrict);
    }
}
