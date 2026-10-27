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
    Task<TestAttemptOperationResult> GradeAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid attemptId, Guid answerId, GradeTestAnswerRequest request,
        CancellationToken cancellationToken = default);
    Task<TestAttemptOperationResult> FinalizeAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid attemptId, CancellationToken cancellationToken = default);
    Task<TestAttemptOperationResult> GetHistoryAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid attemptId, CancellationToken cancellationToken = default);
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

public sealed record TestAttemptOperationResult(
    TestAttemptOperationOutcome Outcome,
    GradeTestAnswerResponse? Grade = null,
    FinalizeTestAttemptResponse? Finalization = null,
    TestAttemptGradeHistoryResponse? History = null,
    string? ErrorCode = null,
    string? Error = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null);

public enum TestAttemptOperationOutcome
{
    Succeeded,
    NotFound,
    Forbidden,
    Invalid,
    Conflict
}
