using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AnonPulse.Api.Services.Admin;

public sealed class AdminAccessChecker(ApplicationDbContext dbContext)
    : IAdminAccessChecker
{
    public Task<bool> IsActiveAdminAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        string normalizedRole = RoleNames.Admin.ToUpperInvariant();
        return (
                from user in dbContext.Users.AsNoTracking()
                join userRole in dbContext.Set<IdentityUserRole<Guid>>().AsNoTracking()
                    on user.Id equals userRole.UserId
                join role in dbContext.Roles.AsNoTracking()
                    on userRole.RoleId equals role.Id
                where user.Id == userId &&
                      !user.IsAdministrativelyDeactivated &&
                      role.NormalizedName == normalizedRole
                select user.Id)
            .AnyAsync(cancellationToken);
    }
}
