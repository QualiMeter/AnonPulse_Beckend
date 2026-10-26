namespace AnonPulse.Api.Models;

public sealed class PollQuestionCorrectOption
{
    public Guid QuestionId { get; set; }
    public Guid OptionId { get; set; }
    public PollQuestion Question { get; set; } = null!;
    public PollOption Option { get; set; } = null!;
}
