using AnonPulse.Api.Contracts.Admin;

namespace AnonPulse.Api.Services.Admin;

public interface IAdminDashboardService
{
    Task<AdminDashboardResponse> GetAsync(
        CancellationToken cancellationToken = default);
}
