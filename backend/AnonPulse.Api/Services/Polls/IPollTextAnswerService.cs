using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;

namespace AnonPulse.Api.Services.Polls;

public interface IPollTextAnswerService
{
    Task<PollTextAnswersResult> GetAsync(Guid actorId, bool isAdmin, Guid pollId,
        Guid questionId, int page, int pageSize, CancellationToken cancellationToken = default);
}

public enum PollTextAnswersOutcome { Succeeded, NotFound, Conflict }

public sealed record PollTextAnswersResult(
    PollTextAnswersOutcome Outcome,
    PagedResponse<PollTextAnswerResponse>? Response = null,
    string? Error = null);
