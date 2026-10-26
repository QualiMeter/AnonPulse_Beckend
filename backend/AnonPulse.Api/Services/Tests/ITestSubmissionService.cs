using AnonPulse.Api.Contracts.PublicPolls;

namespace AnonPulse.Api.Services.Tests;

public interface ITestSubmissionService
{
    Task<TestSubmissionResult> SubmitAsync(
        Guid pollId,
        SubmitTestRequest request,
        CancellationToken cancellationToken = default);
}

public enum TestSubmissionOutcome
{
    Succeeded,
    NotFound,
    Invalid,
    Conflict
}

public sealed record TestSubmissionResult(
    TestSubmissionOutcome Outcome,
    TestSubmissionCreatedResponse? Response = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null,
    string? Error = null,
    string? Code = null);
