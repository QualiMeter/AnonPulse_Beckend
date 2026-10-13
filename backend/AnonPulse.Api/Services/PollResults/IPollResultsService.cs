using AnonPulse.Api.Contracts.PollResults;

namespace AnonPulse.Api.Services.PollResults;

public interface IPollResultsService
{
    Task<PollResultsResult> GetAsync(
        Guid pollId,
        CancellationToken cancellationToken = default);
}

public enum PollResultsOutcome
{
    Succeeded,
    NotFound,
    NotPublished
}

public sealed record PollResultsResult(
    PollResultsOutcome Outcome,
    PollResultsResponse? Results = null);
