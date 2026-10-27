namespace AnonPulse.Api.Models;

public sealed class TestGradeChange
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public Guid AnswerId { get; set; }
    public Guid ActorId { get; set; }
    public decimal? OldScore { get; set; }
    public decimal NewScore { get; set; }
    public required string Source { get; set; }
    public Guid? KnowledgeGradingSuggestionId { get; set; }
    public DateTime ChangedAt { get; set; }
    public NamedAttempt Attempt { get; set; } = null!;
    public NamedAttemptAnswer Answer { get; set; } = null!;
    public PollKnowledgeGradingSuggestion? KnowledgeGradingSuggestion { get; set; }
}

public static class TestGradeChangeSources
{
    public const string Manual = "manual";
    public const string AiSuggestion = "ai_suggestion";
}
