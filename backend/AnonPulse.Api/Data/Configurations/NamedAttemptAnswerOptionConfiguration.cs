using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class NamedAttemptAnswerOptionConfiguration
    : IEntityTypeConfiguration<NamedAttemptAnswerOption>
{
    public void Configure(EntityTypeBuilder<NamedAttemptAnswerOption> builder)
    {
        builder.ToTable("named_attempt_answer_options");
        builder.HasKey(item => new { item.AnswerId, item.OptionId });
        builder.Property(item => item.AnswerId).HasColumnName("answer_id");
        builder.Property(item => item.OptionId).HasColumnName("option_id");
        builder.HasOne(item => item.Answer).WithMany(answer => answer.SelectedOptions)
            .HasForeignKey(item => item.AnswerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Option).WithMany(option => option.TestAnswerSelections)
            .HasForeignKey(item => item.OptionId).OnDelete(DeleteBehavior.Restrict);
    }
}
