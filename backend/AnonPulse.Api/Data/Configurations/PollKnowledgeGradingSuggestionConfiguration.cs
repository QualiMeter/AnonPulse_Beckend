using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class PollKnowledgeGradingSuggestionConfiguration
    : IEntityTypeConfiguration<PollKnowledgeGradingSuggestion>
{
    public void Configure(EntityTypeBuilder<PollKnowledgeGradingSuggestion> builder)
    {
        builder.ToTable("poll_knowledge_grading_suggestions", table =>
        {
            table.HasCheckConstraint("ck_knowledge_grading_suggestions_score",
                "max_score BETWEEN 1 AND 100 AND suggested_score BETWEEN 0 AND max_score");
            table.HasCheckConstraint("ck_knowledge_grading_suggestions_explanation",
                "btrim(explanation) <> ''");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.JobId).HasColumnName("job_id");
        builder.Property(item => item.AnswerId).HasColumnName("answer_id");
        builder.Property(item => item.SuggestedScore).HasColumnName("suggested_score");
        builder.Property(item => item.MaxScore).HasColumnName("max_score");
        builder.Property(item => item.Explanation).HasColumnName("explanation").HasColumnType("text");
        builder.Property(item => item.CorrectPointsJson).HasColumnName("correct_points").HasColumnType("jsonb");
        builder.Property(item => item.MissingOrIncorrectPointsJson).HasColumnName("missing_or_incorrect_points").HasColumnType("jsonb");
        builder.Property(item => item.NeedsReview).HasColumnName("needs_review");
        builder.Property(item => item.ReviewReasonsJson).HasColumnName("review_reasons").HasColumnType("jsonb");
        builder.Property(item => item.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.HasIndex(item => new { item.JobId, item.AnswerId }).IsUnique()
            .HasDatabaseName("ux_knowledge_grading_suggestions_job_answer");
        builder.HasIndex(item => item.AnswerId);
        builder.HasOne(item => item.Job).WithMany(job => job.Suggestions)
            .HasForeignKey(item => item.JobId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Answer).WithMany()
            .HasForeignKey(item => item.AnswerId).OnDelete(DeleteBehavior.Cascade);
    }
}
