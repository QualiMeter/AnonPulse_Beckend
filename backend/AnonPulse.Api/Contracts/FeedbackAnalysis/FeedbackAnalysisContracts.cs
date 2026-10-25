namespace AnonPulse.Api.Contracts.FeedbackAnalysis;

public sealed record StartFeedbackAnalysisRequest(bool ExternalProcessingConfirmed);

public sealed record FeedbackAnalysisResponse(
    Guid Id,
    Guid PollId,
    Guid QuestionId,
    string Status,
    int AnswerCount,
    FeedbackReportResponse? Report,
    string? ErrorCode,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public sealed record FeedbackReportResponse(
    string Summary,
    IReadOnlyList<FeedbackThemeResponse> Themes,
    IReadOnlyList<FeedbackRecommendationResponse> Recommendations,
    IReadOnlyList<string> Limitations);

public sealed record FeedbackThemeResponse(
    string Name,
    string Description,
    string Category,
    int MentionCount);

public sealed record FeedbackRecommendationResponse(
    string Title,
    string Description,
    string Priority);
