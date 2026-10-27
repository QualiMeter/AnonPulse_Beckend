using AnonPulse.Api.Contracts.KnowledgeGrading;

namespace AnonPulse.Api.Services.KnowledgeGrading;

public interface IKnowledgeGradingService
{
    Task<KnowledgeGradingServiceResult> StartAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid questionId, bool externalProcessingConfirmed,
        CancellationToken cancellationToken);
    Task<KnowledgeGradingServiceResult> GetAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid questionId, CancellationToken cancellationToken);
    Task<KnowledgeGradingServiceResult> GetAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid questionId, Guid jobId, CancellationToken cancellationToken);
    Task<KnowledgeGradingServiceResult> GetResultsAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid questionId, int page, int pageSize,
        CancellationToken cancellationToken);
    Task<KnowledgeGradingServiceResult> GetResultsAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid questionId, Guid jobId, int page, int pageSize,
        CancellationToken cancellationToken);
}

public sealed record KnowledgeGradingServiceResult(
    KnowledgeGradingOutcome Outcome,
    KnowledgeGradingJobResponse? Job = null,
    KnowledgeGradingResultsResponse? Results = null,
    string? ErrorCode = null,
    string? Error = null,
    int? RetryAfterSeconds = null);

public enum KnowledgeGradingOutcome
{
    Succeeded,
    NotFound,
    Forbidden,
    Conflict,
    Invalid,
    Disabled,
    TooLarge,
    RateLimited
}
