namespace AnonPulse.Api.Contracts.KnowledgeGrading;

public sealed record StartKnowledgeGradingRequest(bool ExternalProcessingConfirmed);

public sealed record KnowledgeGradingJobResponse(
    Guid Id,
    Guid PollId,
    Guid QuestionId,
    string Status,
    int AnswerCount,
    int SuggestedCount,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorCode,
    string Model,
    string AlgorithmVersion);

public sealed record KnowledgeGradingResultsResponse(
    Guid JobId,
    string Model,
    string AlgorithmVersion,
    IReadOnlyList<KnowledgeGradingSuggestionResponse> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed record KnowledgeGradingSuggestionResponse(
    Guid AnswerId,
    int SuggestedScore,
    int MaxScore,
    string Explanation,
    IReadOnlyList<string> CorrectPoints,
    IReadOnlyList<string> MissingOrIncorrectPoints,
    bool NeedsReview,
    IReadOnlyList<string> ReviewReasons,
    string Model,
    string AlgorithmVersion);
