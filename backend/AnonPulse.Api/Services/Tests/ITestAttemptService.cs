using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;

namespace AnonPulse.Api.Services.Tests;

public interface ITestAttemptService
{
    Task<TestAttemptsResult> GetAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

public enum TestAttemptsOutcome
{
    Succeeded,
    NotFound,
    Conflict
}

public sealed record TestAttemptsResult(
    TestAttemptsOutcome Outcome,
    PagedResponse<TestAttemptResponse>? Response = null,
    string? Error = null);
