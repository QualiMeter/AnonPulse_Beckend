using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class PollQuestionKnowledgeSettingsConfiguration
    : IEntityTypeConfiguration<PollQuestionKnowledgeSettings>
{
    public void Configure(EntityTypeBuilder<PollQuestionKnowledgeSettings> builder)
    {
        builder.ToTable("poll_question_knowledge_settings", table =>
        {
            table.HasCheckConstraint("ck_poll_question_knowledge_settings_max_score", "max_score BETWEEN 1 AND 10");
            table.HasCheckConstraint("ck_poll_question_knowledge_settings_expected_answer", "btrim(expected_answer) <> ''");
            table.HasCheckConstraint("ck_poll_question_knowledge_settings_grading_criteria", "btrim(grading_criteria) <> ''");
        });
        builder.HasKey(settings => settings.QuestionId);
        builder.Property(settings => settings.QuestionId).HasColumnName("question_id");
        builder.Property(settings => settings.ExpectedAnswer).HasColumnName("expected_answer").HasMaxLength(4000).IsRequired();
        builder.Property(settings => settings.GradingCriteria).HasColumnName("grading_criteria").HasMaxLength(4000).IsRequired();
        builder.Property(settings => settings.MaxScore).HasColumnName("max_score").IsRequired();
        builder.HasOne(settings => settings.Question).WithOne(question => question.KnowledgeSettings)
            .HasForeignKey<PollQuestionKnowledgeSettings>(settings => settings.QuestionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
