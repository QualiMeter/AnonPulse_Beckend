using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Models;

namespace AnonPulse.Api.Services.Admin;

public interface IAdminPollService
{
    Task<PagedResponse<AdminPollListItemResponse>> GetAllAsync(
        int page,
        int pageSize,
        string? search,
        Guid? creatorId,
        PollStatus? status,
        DateTime? createdFromUtc,
        DateTime? createdToExclusiveUtc,
        CancellationToken cancellationToken = default);

    Task<AdminPollDetailsResponse?> GetByIdAsync(
        Guid pollId,
        CancellationToken cancellationToken = default);
}
