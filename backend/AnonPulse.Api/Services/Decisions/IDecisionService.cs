using AnonPulse.Api.Contracts.Decisions;

namespace AnonPulse.Api.Services.Decisions;

public interface IDecisionService
{
    Task<DecisionUpsertResult> UpsertAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        UpsertDecisionRequest request,
        CancellationToken cancellationToken = default);

    Task<PublishedDecisionResult> GetPublishedAsync(
        Guid pollId,
        CancellationToken cancellationToken = default);
}

public enum DecisionUpsertOutcome
{
    Succeeded,
    Invalid,
    NotFound,
    Conflict
}

public sealed record DecisionUpsertResult(
    DecisionUpsertOutcome Outcome,
    DecisionResponse? Decision = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null,
    string? Error = null);

public enum PublishedDecisionOutcome
{
    Succeeded,
    NotFound
}

public sealed record PublishedDecisionResult(
    PublishedDecisionOutcome Outcome,
    DecisionResponse? Decision = null);
