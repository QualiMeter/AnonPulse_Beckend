namespace AnonPulse.Api.Services.Admin;

public interface IAdminAccessChecker
{
    Task<bool> IsActiveAdminAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
