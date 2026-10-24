namespace AnonPulse.Api.Models;

public sealed class PollQuestionKnowledgeSettings
{
    public Guid QuestionId { get; set; }
    public required string ExpectedAnswer { get; set; }
    public required string GradingCriteria { get; set; }
    public int MaxScore { get; set; }
    public PollQuestion Question { get; set; } = null!;
}
