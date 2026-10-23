using System.Text.Json;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonPulse.Api.Services.Admin;

public sealed class AdminAuditService(ApplicationDbContext dbContext)
    : IAdminAuditService
{
    public async Task<PagedResponse<AdminAuditResponse>> GetAllAsync(
        int page,
        int pageSize,
        string? actionType,
        string? targetType,
        Guid? targetId,
        DateTime? fromUtc,
        DateTime? toExclusiveUtc,
        CancellationToken cancellationToken = default)
    {
        IQueryable<AdminAuditEvent> query = dbContext.AdminAuditEvents.AsNoTracking();
        string action = actionType?.Trim() ?? string.Empty;
        string target = targetType?.Trim() ?? string.Empty;
        if (action.Length > 0)
        {
            query = query.Where(item => item.ActionType == action);
        }

        if (target.Length > 0)
        {
            query = query.Where(item => item.TargetType == target);
        }

        if (targetId is not null)
        {
            query = query.Where(item => item.TargetId == targetId.Value);
        }

        if (fromUtc is not null)
        {
            query = query.Where(item => item.OccurredAt >= fromUtc.Value);
        }

        if (toExclusiveUtc is not null)
        {
            query = query.Where(item => item.OccurredAt < toExclusiveUtc.Value);
        }

        int totalCount = await query.CountAsync(cancellationToken);
        List<AdminAuditEvent> events = await query
            .OrderByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        AdminAuditResponse[] items = events.Select(item => new AdminAuditResponse(
            item.Id,
            item.AdministratorId,
            item.ActionType,
            item.TargetType,
            item.TargetId,
            item.OccurredAt,
            JsonSerializer.Deserialize<Dictionary<string, string>>(item.MetadataJson)
                ?? []))
            .ToArray();

        return new PagedResponse<AdminAuditResponse>(
            items,
            page,
            pageSize,
            totalCount,
            totalCount == 0 ? 0 : (totalCount + pageSize - 1) / pageSize);
    }
}
