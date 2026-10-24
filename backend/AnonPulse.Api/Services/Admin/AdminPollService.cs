using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonPulse.Api.Services.Admin;

public sealed class AdminPollService(ApplicationDbContext dbContext)
    : IAdminPollService
{
    public async Task<PagedResponse<AdminPollListItemResponse>> GetAllAsync(
        int page,
        int pageSize,
        string? search,
        Guid? creatorId,
        PollStatus? status,
        DateTime? createdFromUtc,
        DateTime? createdToExclusiveUtc,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Poll> query = dbContext.Polls.AsNoTracking();
        string normalizedSearch = search?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedSearch.Length > 0)
        {
            query = query.Where(poll =>
                (poll.Title != null && poll.Title.ToLower().Contains(normalizedSearch)) ||
                poll.Questions.Any(question =>
                    question.Text.ToLower().Contains(normalizedSearch)));
        }

        if (creatorId is not null)
        {
            query = query.Where(poll => poll.CreatorId == creatorId.Value);
        }

        if (status is not null)
        {
            query = query.Where(poll => poll.Status == status.Value);
        }

        if (createdFromUtc is not null)
        {
            query = query.Where(poll => poll.CreatedAt >= createdFromUtc.Value);
        }

        if (createdToExclusiveUtc is not null)
        {
            query = query.Where(poll => poll.CreatedAt < createdToExclusiveUtc.Value);
        }

        int totalCount = await query.CountAsync(cancellationToken);
        List<AdminPollListItemResponse> items = await query
            .OrderByDescending(poll => poll.CreatedAt)
            .ThenByDescending(poll => poll.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(poll => new AdminPollListItemResponse(
                poll.Id,
                poll.Title,
                poll.Status.ToString().ToLower(),
                poll.CreatorId,
                poll.Creator.Email,
                poll.CreatedAt,
                poll.PublishedAt,
                poll.ClosedAt,
                poll.Questions.Count,
                poll.VoteParticipations.Count,
                poll.Decision != null && poll.Decision.PublishedAt != null))
            .ToListAsync(cancellationToken);

        return new PagedResponse<AdminPollListItemResponse>(
            items,
            page,
            pageSize,
            totalCount,
            totalCount == 0 ? 0 : (totalCount + pageSize - 1) / pageSize);
    }

    public async Task<AdminPollDetailsResponse?> GetByIdAsync(
        Guid pollId,
        CancellationToken cancellationToken = default)
    {
        Poll? poll = await dbContext.Polls
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Creator)
            .Include(item => item.Questions)
            .ThenInclude(question => question.Options)
            .Include(item => item.VoteParticipations)
            .Include(item => item.Decision)
            .SingleOrDefaultAsync(item => item.Id == pollId, cancellationToken);
        if (poll is null)
        {
            return null;
        }

        int voteCount = await dbContext.Votes.CountAsync(
            vote => vote.Option.Question.PollId == pollId,
            cancellationToken);
        PollQuestionResponse[] questions = poll.Questions
            .OrderBy(question => question.Position)
            .ThenBy(question => question.Id)
            .Select(question => new PollQuestionResponse(
                question.Id,
                question.Text,
                question.Position,
                question.Options
                    .OrderBy(option => option.Position)
                    .ThenBy(option => option.Id)
                    .Select(option => new PollOptionResponse(
                        option.Id,
                        option.Text,
                        option.Position))
                    .ToArray(),
                question.AllowsMultipleAnswers))
            .ToArray();

        return new AdminPollDetailsResponse(
            poll.Id,
            poll.Title,
            poll.Status.ToString().ToLowerInvariant(),
            poll.CreatorId,
            poll.Creator.Email,
            poll.CreatedAt,
            poll.PublishedAt,
            poll.ClosedAt,
            questions,
            poll.VoteParticipations.Count,
            voteCount,
            poll.Decision?.PublishedAt is not null);
    }
}
