using AnonPulse.Api.Contracts.FeedbackAnalysis;

namespace AnonPulse.Api.Services.FeedbackAnalysis;

public interface IFeedbackAnalysisService
{
    Task<FeedbackAnalysisServiceResult> StartAsync(
        Guid actorId, bool isAdmin, Guid pollId, Guid questionId,
        bool externalProcessingConfirmed, CancellationToken cancellationToken);
    Task<FeedbackAnalysisServiceResult> GetAsync(
        Guid actorId, bool isAdmin, Guid pollId, Guid questionId,
        CancellationToken cancellationToken);
}

public sealed record FeedbackAnalysisServiceResult(
    FeedbackAnalysisOutcome Outcome,
    FeedbackAnalysisResponse? Analysis = null,
    string? ErrorCode = null,
    string? Error = null);

public enum FeedbackAnalysisOutcome
{
    Succeeded,
    NotFound,
    Forbidden,
    Conflict,
    Invalid,
    Disabled,
    TooLarge
}
