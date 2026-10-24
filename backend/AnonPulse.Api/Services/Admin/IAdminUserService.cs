using AnonPulse.Api.Contracts.Admin;

namespace AnonPulse.Api.Services.Admin;

public interface IAdminUserService
{
    Task<PagedResponse<AdminUserListItemResponse>> GetAllAsync(
        int page,
        int pageSize,
        string? search,
        string? role,
        bool? isDeactivated,
        CancellationToken cancellationToken = default);

    Task<AdminUserDetailsResponse?> GetByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<AdminUserMutationResult> SetDeactivatedAsync(
        Guid administratorId,
        Guid userId,
        bool isDeactivated,
        CancellationToken cancellationToken = default);

    Task<AdminUserMutationResult> DeleteAsync(
        Guid administratorId,
        Guid userId,
        CancellationToken cancellationToken = default);
}

public enum AdminUserMutationOutcome
{
    Succeeded,
    NotFound,
    Conflict,
    Failed
}

public sealed record AdminUserMutationResult(
    AdminUserMutationOutcome Outcome,
    AdminUserDetailsResponse? User = null,
    string? Code = null,
    string? Error = null,
    int PollDependencies = 0,
    int DecisionDependencies = 0);
