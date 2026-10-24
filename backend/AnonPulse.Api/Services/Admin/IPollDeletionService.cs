namespace AnonPulse.Api.Services.Admin;

public interface IPollDeletionService
{
    Task<PollDeletionResult> DeleteTeacherDraftAsync(
        Guid teacherId,
        Guid pollId,
        CancellationToken cancellationToken = default);

    Task<PollDeletionResult> DeleteAdminAsync(
        Guid administratorId,
        Guid pollId,
        bool confirmed,
        CancellationToken cancellationToken = default);
}

public enum PollDeletionOutcome
{
    Succeeded,
    NotFound,
    Conflict
}

public sealed record PollDeletionResult(
    PollDeletionOutcome Outcome,
    string? Code = null,
    string? Error = null);
