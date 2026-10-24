using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AnonPulse.Api.Services.Admin;

public sealed class AdminDashboardService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IAdminDashboardService
{
    public async Task<AdminDashboardResponse> GetAsync(
        CancellationToken cancellationToken = default)
    {
        int totalUsers = await dbContext.Users.CountAsync(cancellationToken);
        int blockedUsers = await dbContext.Users.CountAsync(
            user => user.IsAdministrativelyDeactivated,
            cancellationToken);
        List<RoleCountResponse> roleCounts = await (
                from userRole in dbContext.Set<IdentityUserRole<Guid>>()
                join role in dbContext.Roles on userRole.RoleId equals role.Id
                group userRole by role.Name into grouped
                orderby grouped.Key
                select new RoleCountResponse(grouped.Key!, grouped.Count()))
            .ToListAsync(cancellationToken);

        List<PollStatusCount> pollStatusCounts = await dbContext.Polls
            .GroupBy(poll => poll.Status)
            .Select(grouped => new PollStatusCount(grouped.Key, grouped.Count()))
            .ToListAsync(cancellationToken);
        int totalPolls = pollStatusCounts.Sum(item => item.Count);
        int totalParticipations = await dbContext.VoteParticipations
            .CountAsync(cancellationToken);
        int pollsWithPublishedDecisions = await dbContext.Decisions.CountAsync(
            decision => decision.PublishedAt != null,
            cancellationToken);

        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        DateTime firstDay = today.AddDays(-29).ToDateTime(
            TimeOnly.MinValue,
            DateTimeKind.Utc);
        List<DailyCount> storedDailyCounts = await dbContext.Polls
            .Where(poll => poll.CreatedAt >= firstDay)
            .GroupBy(poll => poll.CreatedAt.Date)
            .Select(grouped => new DailyCount(grouped.Key, grouped.Count()))
            .ToListAsync(cancellationToken);
        Dictionary<DateOnly, int> countByDate = storedDailyCounts.ToDictionary(
            item => DateOnly.FromDateTime(item.Date),
            item => item.Count);
        DailyPollCreationResponse[] dailyCounts = Enumerable.Range(0, 30)
            .Select(offset => today.AddDays(offset - 29))
            .Select(date => new DailyPollCreationResponse(
                date,
                countByDate.GetValueOrDefault(date)))
            .ToArray();

        return new AdminDashboardResponse(
            new AdminUserSummaryResponse(
                totalUsers,
                totalUsers - blockedUsers,
                blockedUsers,
                roleCounts),
            new AdminPollSummaryResponse(
                totalPolls,
                GetPollCount(PollStatus.Draft),
                GetPollCount(PollStatus.Open),
                GetPollCount(PollStatus.Closed),
                pollsWithPublishedDecisions),
            totalParticipations,
            dailyCounts);

        int GetPollCount(PollStatus status) =>
            pollStatusCounts.SingleOrDefault(item => item.Status == status)?.Count ?? 0;
    }

    private sealed record PollStatusCount(PollStatus Status, int Count);

    private sealed record DailyCount(DateTime Date, int Count);
}
