using AnonPulse.Api.Contracts.Admin;

namespace AnonPulse.Api.Services.Admin;

public interface IAdminAuditService
{
    Task<PagedResponse<AdminAuditResponse>> GetAllAsync(
        int page,
        int pageSize,
        string? actionType,
        string? targetType,
        Guid? targetId,
        DateTime? fromUtc,
        DateTime? toExclusiveUtc,
        CancellationToken cancellationToken = default);
}
