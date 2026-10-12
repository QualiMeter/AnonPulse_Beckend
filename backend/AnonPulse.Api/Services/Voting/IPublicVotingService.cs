using AnonPulse.Api.Contracts.PublicPolls;

namespace AnonPulse.Api.Services.Voting;

public interface IPublicVotingService
{
    Task<PublicPollResult> GetOpenPollAsync(
        Guid pollId,
        CancellationToken cancellationToken = default);

    Task<VoteSubmissionResult> SubmitAsync(
        Guid pollId,
        SubmitVotesRequest request,
        string tokenHash,
        CancellationToken cancellationToken = default);
}

public enum PublicPollOutcome
{
    Succeeded,
    NotFound,
    NotOpen
}

public sealed record PublicPollResult(
    PublicPollOutcome Outcome,
    PublicPollResponse? Poll = null);

public enum VoteSubmissionOutcome
{
    Succeeded,
    NotFound,
    Conflict,
    Invalid
}

public sealed record VoteSubmissionResult(
    VoteSubmissionOutcome Outcome,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null,
    string? Error = null);
