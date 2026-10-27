using AnonPulse.Api.Contracts.PublicPolls;

namespace AnonPulse.Api.Services.Tests;

public interface IPublicTestResultService
{
    Task<PublicTestResultServiceResult> GetAsync(Guid pollId, Guid attemptId,
        string? accessSecret, CancellationToken cancellationToken = default);
}

public sealed record PublicTestResultServiceResult(
    PublicTestResultOutcome Outcome,
    PublicTestResultResponse? Response = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null);

public enum PublicTestResultOutcome
{
    Succeeded,
    Invalid,
    NotFound
}
