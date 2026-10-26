using AnonPulse.Api.Contracts.PollResults;

namespace AnonPulse.Api.Services.PollResults;

public interface IPollResultsService
{
    Task<PollResultsResult> GetAsync(
        Guid pollId,
        PollResultsAccessContext accessContext,
        CancellationToken cancellationToken = default);
}

public sealed record PollResultsAccessContext(
    Guid? UserId,
    bool IsAuthenticated,
    bool IsTeacher,
    bool IsAdmin);

public enum PollResultsOutcome
{
    Succeeded,
    NotFound,
    NotPublished,
    NotAvailableForTest,
    Unauthorized,
    Forbidden
}

public sealed record PollResultsResult(
    PollResultsOutcome Outcome,
    PollResultsResponse? Results = null);
