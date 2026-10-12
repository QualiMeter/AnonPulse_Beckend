using AnonPulse.Api.Contracts.Polls;

namespace AnonPulse.Api.Services.Polls;

public interface IPollService
{
    Task<PollCreationResult> CreateAsync(
        Guid creatorId,
        CreatePollRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PollResponse>> GetAllAsync(
        Guid creatorId,
        CancellationToken cancellationToken = default);

    Task<PollResponse?> GetByIdAsync(
        Guid creatorId,
        Guid pollId,
        CancellationToken cancellationToken = default);

    Task<PollTransitionResult> PublishAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        CancellationToken cancellationToken = default);

    Task<PollTransitionResult> CloseAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        CancellationToken cancellationToken = default);
}

public sealed record PollTransitionResult(
    PollTransitionOutcome Outcome,
    PollResponse? Poll = null,
    string? Error = null);

public enum PollTransitionOutcome
{
    Succeeded,
    NotFound,
    Conflict
}

public sealed record PollCreationResult(
    PollResponse? Poll,
    IReadOnlyDictionary<string, string[]> ValidationErrors)
{
    public bool Succeeded => Poll is not null;
}
