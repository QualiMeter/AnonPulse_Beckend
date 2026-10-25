namespace AnonPulse.Api.Models;

public sealed class PollFeedbackAnalysis
{
    public Guid Id { get; set; }
    public Guid PollId { get; set; }
    public Guid QuestionId { get; set; }
    public FeedbackAnalysisStatus Status { get; set; } = FeedbackAnalysisStatus.Pending;
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public required string AlgorithmVersion { get; set; }
    public int AnswerCount { get; set; }
    public required string AnswerFingerprint { get; set; }
    public string? ReportJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public string? ErrorCode { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public Poll Poll { get; set; } = null!;
    public PollQuestion Question { get; set; } = null!;
}

public enum FeedbackAnalysisStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}
