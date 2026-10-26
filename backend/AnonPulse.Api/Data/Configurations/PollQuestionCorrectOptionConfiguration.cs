using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class PollQuestionCorrectOptionConfiguration
    : IEntityTypeConfiguration<PollQuestionCorrectOption>
{
    public void Configure(EntityTypeBuilder<PollQuestionCorrectOption> builder)
    {
        builder.ToTable("poll_question_correct_options");
        builder.HasKey(item => new { item.QuestionId, item.OptionId });
        builder.Property(item => item.QuestionId).HasColumnName("question_id");
        builder.Property(item => item.OptionId).HasColumnName("option_id");
        builder.HasOne(item => item.Question)
            .WithMany(question => question.CorrectOptions)
            .HasForeignKey(item => item.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Option)
            .WithMany(option => option.CorrectForQuestions)
            .HasForeignKey(item => new { item.QuestionId, item.OptionId })
            .HasPrincipalKey(option => new { option.QuestionId, option.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
