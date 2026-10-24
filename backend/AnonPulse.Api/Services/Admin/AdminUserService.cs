using System.Text.Json;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AnonPulse.Api.Services.Admin;

public sealed class AdminUserService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider) : IAdminUserService
{
    private static readonly string NormalizedAdminRole =
        RoleNames.Admin.ToUpperInvariant();

    public async Task<PagedResponse<AdminUserListItemResponse>> GetAllAsync(
        int page,
        int pageSize,
        string? search,
        string? role,
        bool? isDeactivated,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ApplicationUser> query = dbContext.Users.AsNoTracking();
        string normalizedSearch = search?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedSearch.Length > 0)
        {
            query = query.Where(user =>
                user.NormalizedEmail != null &&
                user.NormalizedEmail.Contains(normalizedSearch));
        }

        if (isDeactivated is not null)
        {
            query = query.Where(user =>
                user.IsAdministrativelyDeactivated == isDeactivated.Value);
        }

        string normalizedRole = role?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedRole.Length > 0)
        {
            query = query.Where(user =>
                dbContext.Set<IdentityUserRole<Guid>>()
                    .Where(userRole => userRole.UserId == user.Id)
                    .Join(
                        dbContext.Roles,
                        userRole => userRole.RoleId,
                        identityRole => identityRole.Id,
                        (_, identityRole) => identityRole)
                    .Any(identityRole => identityRole.NormalizedName == normalizedRole));
        }

        int totalCount = await query.CountAsync(cancellationToken);
        List<ApplicationUser> users = await query
            .OrderBy(user => user.NormalizedEmail)
            .ThenBy(user => user.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        Dictionary<Guid, string[]> rolesByUser = await GetRolesAsync(
            users.Select(user => user.Id).ToArray(),
            cancellationToken);

        AdminUserListItemResponse[] items = users.Select(user =>
            new AdminUserListItemResponse(
                user.Id,
                user.Email,
                rolesByUser.GetValueOrDefault(user.Id, []),
                user.IsAdministrativelyDeactivated,
                user.LockoutEnd))
            .ToArray();

        return new PagedResponse<AdminUserListItemResponse>(
            items,
            page,
            pageSize,
            totalCount,
            CalculateTotalPages(totalCount, pageSize));
    }

    public async Task<AdminUserDetailsResponse?> GetByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ApplicationUser? user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        return user is null
            ? null
            : await ToDetailsAsync(user, cancellationToken);
    }

    public async Task<AdminUserMutationResult> SetDeactivatedAsync(
        Guid administratorId,
        Guid userId,
        bool isDeactivated,
        CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction? transaction = await BeginTransactionAsync(
            cancellationToken);
        await LockAdminRoleAsync(transaction, cancellationToken);
        ApplicationUser? user = await LockUserAsync(userId, transaction, cancellationToken);
        if (user is null)
        {
            await RollbackAsync(transaction, cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationOutcome.NotFound);
        }

        if (administratorId == userId && isDeactivated)
        {
            await RollbackAsync(transaction, cancellationToken);
            return Conflict(
                "self_deactivation_forbidden",
                "An administrator cannot deactivate their own account.");
        }

        bool isAdmin = await IsAdminAsync(userId, cancellationToken);
        if (isDeactivated && isAdmin &&
            await CountOtherActiveAdminsAsync(userId, cancellationToken) == 0)
        {
            await RollbackAsync(transaction, cancellationToken);
            return Conflict(
                "last_active_admin",
                "The operation would leave the system without an active administrator.");
        }

        if (user.IsAdministrativelyDeactivated == isDeactivated)
        {
            await CommitAsync(transaction, cancellationToken);
            return new AdminUserMutationResult(
                AdminUserMutationOutcome.Succeeded,
                await ToDetailsAsync(user, cancellationToken));
        }

        user.IsAdministrativelyDeactivated = isDeactivated;
        IdentityResult updateResult = await userManager.UpdateSecurityStampAsync(user);
        if (!updateResult.Succeeded)
        {
            await RollbackAsync(transaction, CancellationToken.None);
            return new AdminUserMutationResult(
                AdminUserMutationOutcome.Failed,
                Error: FormatErrors(updateResult));
        }

        AddAuditEvent(
            administratorId,
            isDeactivated
                ? AdminAuditActionTypes.UserDeactivated
                : AdminAuditActionTypes.UserReactivated,
            AdminAuditTargetTypes.User,
            userId,
            new Dictionary<string, string>
            {
                ["result"] = isDeactivated ? "deactivated" : "reactivated"
            });
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        return new AdminUserMutationResult(
            AdminUserMutationOutcome.Succeeded,
            await ToDetailsAsync(user, cancellationToken));
    }

    public async Task<AdminUserMutationResult> DeleteAsync(
        Guid administratorId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction? transaction = await BeginTransactionAsync(
            cancellationToken);
        await LockAdminRoleAsync(transaction, cancellationToken);
        ApplicationUser? user = await LockUserAsync(userId, transaction, cancellationToken);
        if (user is null)
        {
            await RollbackAsync(transaction, cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationOutcome.NotFound);
        }

        if (administratorId == userId)
        {
            await RollbackAsync(transaction, cancellationToken);
            return Conflict(
                "self_deletion_forbidden",
                "An administrator cannot delete their own account.");
        }

        int pollDependencies = await dbContext.Polls.CountAsync(
            poll => poll.CreatorId == userId,
            cancellationToken);
        int decisionDependencies = await dbContext.Decisions.CountAsync(
            decision => decision.AuthorId == userId,
            cancellationToken);
        if (pollDependencies > 0 || decisionDependencies > 0)
        {
            await RollbackAsync(transaction, cancellationToken);
            return new AdminUserMutationResult(
                AdminUserMutationOutcome.Conflict,
                Code: "user_has_dependencies",
                Error: "The user has business records and must be deactivated instead.",
                PollDependencies: pollDependencies,
                DecisionDependencies: decisionDependencies);
        }

        bool isAdmin = await IsAdminAsync(userId, cancellationToken);
        if (isAdmin && !user.IsAdministrativelyDeactivated &&
            await CountOtherActiveAdminsAsync(userId, cancellationToken) == 0)
        {
            await RollbackAsync(transaction, cancellationToken);
            return Conflict(
                "last_active_admin",
                "The operation would leave the system without an active administrator.");
        }

        IdentityResult deletionResult = await userManager.DeleteAsync(user);
        if (!deletionResult.Succeeded)
        {
            await RollbackAsync(transaction, CancellationToken.None);
            return new AdminUserMutationResult(
                AdminUserMutationOutcome.Failed,
                Error: FormatErrors(deletionResult));
        }

        AddAuditEvent(
            administratorId,
            AdminAuditActionTypes.UserDeleted,
            AdminAuditTargetTypes.User,
            userId,
            new Dictionary<string, string> { ["result"] = "deleted" });
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return new AdminUserMutationResult(AdminUserMutationOutcome.Succeeded);
    }

    private async Task<AdminUserDetailsResponse> ToDetailsAsync(
        ApplicationUser user,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, string[]> roles = await GetRolesAsync(
            [user.Id],
            cancellationToken);
        int pollCount = await dbContext.Polls.CountAsync(
            poll => poll.CreatorId == user.Id,
            cancellationToken);
        int decisionCount = await dbContext.Decisions.CountAsync(
            decision => decision.AuthorId == user.Id,
            cancellationToken);
        return new AdminUserDetailsResponse(
            user.Id,
            user.Email,
            roles.GetValueOrDefault(user.Id, []),
            user.IsAdministrativelyDeactivated,
            user.LockoutEnd,
            pollCount,
            decisionCount);
    }

    private async Task<Dictionary<Guid, string[]>> GetRolesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        List<UserRoleRow> rows = await (
                from userRole in dbContext.Set<IdentityUserRole<Guid>>().AsNoTracking()
                join role in dbContext.Roles.AsNoTracking()
                    on userRole.RoleId equals role.Id
                where userIds.Contains(userRole.UserId)
                orderby role.Name
                select new UserRoleRow(userRole.UserId, role.Name!))
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(row => row.UserId)
            .ToDictionary(
                grouped => grouped.Key,
                grouped => grouped.Select(row => row.Role).ToArray());
    }

    private Task<bool> IsAdminAsync(Guid userId, CancellationToken cancellationToken) =>
        (from userRole in dbContext.Set<IdentityUserRole<Guid>>()
         join role in dbContext.Roles on userRole.RoleId equals role.Id
         where userRole.UserId == userId && role.NormalizedName == NormalizedAdminRole
         select userRole).AnyAsync(cancellationToken);

    private Task<int> CountOtherActiveAdminsAsync(
        Guid excludedUserId,
        CancellationToken cancellationToken) =>
        (from user in dbContext.Users
         join userRole in dbContext.Set<IdentityUserRole<Guid>>() on user.Id equals userRole.UserId
         join role in dbContext.Roles on userRole.RoleId equals role.Id
         where user.Id != excludedUserId &&
               !user.IsAdministrativelyDeactivated &&
               role.NormalizedName == NormalizedAdminRole
         select user.Id).Distinct().CountAsync(cancellationToken);

    private async Task LockAdminRoleAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is null)
        {
            return;
        }

        await dbContext.Roles
            .FromSqlRaw(
                "SELECT * FROM roles WHERE \"NormalizedName\" = 'ADMIN' FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<ApplicationUser?> LockUserAsync(
        Guid userId,
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        return transaction is null
            ? await dbContext.Users.SingleOrDefaultAsync(
                user => user.Id == userId,
                cancellationToken)
            : await dbContext.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
    }

    private void AddAuditEvent(
        Guid administratorId,
        string actionType,
        string targetType,
        Guid targetId,
        IReadOnlyDictionary<string, string> metadata)
    {
        dbContext.AdminAuditEvents.Add(new AdminAuditEvent
        {
            Id = Guid.NewGuid(),
            AdministratorId = administratorId,
            ActionType = actionType,
            TargetType = targetType,
            TargetId = targetId,
            OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
            MetadataJson = JsonSerializer.Serialize(metadata)
        });
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(
        CancellationToken cancellationToken)
    {
        return dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
    }

    private static Task CommitAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken) =>
        transaction is null
            ? Task.CompletedTask
            : transaction.CommitAsync(cancellationToken);

    private static Task RollbackAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken) =>
        transaction is null
            ? Task.CompletedTask
            : transaction.RollbackAsync(cancellationToken);

    private static AdminUserMutationResult Conflict(string code, string error) =>
        new(AdminUserMutationOutcome.Conflict, Code: code, Error: error);

    private static int CalculateTotalPages(int totalCount, int pageSize) =>
        totalCount == 0 ? 0 : (totalCount + pageSize - 1) / pageSize;

    private static string FormatErrors(IdentityResult result) => string.Join(
        "; ",
        result.Errors.Select(error => $"{error.Code}: {error.Description}"));

    private sealed record UserRoleRow(Guid UserId, string Role);
}
