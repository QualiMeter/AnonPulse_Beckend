namespace AnonPulse.Api.Models;

public sealed class PollKnowledgeGradingJob
{
    public Guid Id { get; set; }
    public Guid PollId { get; set; }
    public Guid QuestionId { get; set; }
    public KnowledgeGradingJobStatus Status { get; set; } = KnowledgeGradingJobStatus.Pending;
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public required string AlgorithmVersion { get; set; }
    public required string SnapshotFingerprint { get; set; }
    public required string ExpectedAnswerSnapshot { get; set; }
    public required string GradingCriteriaSnapshot { get; set; }
    public int MaxScoreSnapshot { get; set; }
    public required string AnswersSnapshotJson { get; set; }
    public int AnswerCount { get; set; }
    public int SuggestedCount { get; set; }
    public int ReservedInputTokens { get; set; }
    public int ReservedOutputTokens { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public int AutomaticAttempts { get; set; }
    public int ManualRetryNumber { get; set; }
    public Guid? PreviousJobId { get; set; }
    public Guid? LeaseId { get; set; }
    public int LeaseGeneration { get; set; }
    public string? ErrorCode { get; set; }
    public Poll Poll { get; set; } = null!;
    public PollQuestion Question { get; set; } = null!;
    public ICollection<PollKnowledgeGradingSuggestion> Suggestions { get; } = [];
}

public enum KnowledgeGradingJobStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}
