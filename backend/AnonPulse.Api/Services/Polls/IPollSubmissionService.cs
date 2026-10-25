using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;

namespace AnonPulse.Api.Services.Polls;

public interface IPollSubmissionService
{
    Task<PollSubmissionsResult> GetAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

public enum PollSubmissionsOutcome
{
    Succeeded,
    NotFound,
    Conflict
}

public sealed record PollSubmissionsResult(
    PollSubmissionsOutcome Outcome,
    PagedResponse<IdentifiedPollSubmissionResponse>? Response = null,
    string? Error = null);
