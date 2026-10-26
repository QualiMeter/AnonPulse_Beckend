namespace AnonPulse.Api.Models;

public sealed class PollKnowledgeGradingSuggestion
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid AnswerId { get; set; }
    public int SuggestedScore { get; set; }
    public int MaxScore { get; set; }
    public required string Explanation { get; set; }
    public required string CorrectPointsJson { get; set; }
    public required string MissingOrIncorrectPointsJson { get; set; }
    public bool NeedsReview { get; set; }
    public required string ReviewReasonsJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public PollKnowledgeGradingJob Job { get; set; } = null!;
    public NamedAttemptAnswer Answer { get; set; } = null!;
}
