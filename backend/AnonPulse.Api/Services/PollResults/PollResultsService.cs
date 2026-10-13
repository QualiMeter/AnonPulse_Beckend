using System.Data;
using AnonPulse.Api.Contracts.PollResults;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AnonPulse.Api.Services.PollResults;

public sealed class PollResultsService(ApplicationDbContext dbContext)
    : IPollResultsService
{
    public async Task<PollResultsResult> GetAsync(
        Guid pollId,
        PollResultsAccessContext accessContext,
        CancellationToken cancellationToken = default)
    {
        if (!dbContext.Database.IsRelational())
        {
            return await ReadSnapshotAsync(pollId, accessContext, cancellationToken);
        }

        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

        PollResultsResult result =
            await ReadSnapshotAsync(pollId, accessContext, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<PollResultsResult> ReadSnapshotAsync(
        Guid pollId,
        PollResultsAccessContext accessContext,
        CancellationToken cancellationToken)
    {
        PollHeader? poll = await dbContext.Polls
            .AsNoTracking()
            .Where(item => item.Id == pollId)
            .Select(item => new PollHeader(item.Id, item.CreatorId, item.Status))
            .SingleOrDefaultAsync(cancellationToken);

        if (poll is null)
        {
            return new PollResultsResult(PollResultsOutcome.NotFound);
        }

        if (poll.Status == PollStatus.Draft)
        {
            return new PollResultsResult(PollResultsOutcome.NotPublished);
        }

        if (poll.Status == PollStatus.Open)
        {
            if (!accessContext.IsAuthenticated || accessContext.UserId is null)
            {
                return new PollResultsResult(PollResultsOutcome.Unauthorized);
            }

            if (!accessContext.IsTeacher && !accessContext.IsAdmin)
            {
                return new PollResultsResult(PollResultsOutcome.Forbidden);
            }

            if (!accessContext.IsAdmin && poll.CreatorId != accessContext.UserId)
            {
                return new PollResultsResult(PollResultsOutcome.NotFound);
            }
        }

        PollQuestion[] questions = await dbContext.PollQuestions
            .AsNoTracking()
            .Where(question => question.PollId == pollId)
            .Include(question => question.Options)
            .AsSingleQuery()
            .OrderBy(question => question.Position)
            .ToArrayAsync(cancellationToken);

        Dictionary<Guid, int> voteCounts = await dbContext.Votes
            .AsNoTracking()
            .Where(vote => vote.Option.Question.PollId == pollId)
            .GroupBy(vote => vote.OptionId)
            .Select(group => new
            {
                OptionId = group.Key,
                Count = group.Count()
            })
            .ToDictionaryAsync(
                item => item.OptionId,
                item => item.Count,
                cancellationToken);

        int totalParticipants = await dbContext.VoteParticipations
            .AsNoTracking()
            .CountAsync(
                participation => participation.PollId == pollId,
                cancellationToken);

        var questionResults = new List<PollQuestionResultsResponse>(questions.Length);
        foreach (PollQuestion question in questions)
        {
            PollOption[] options = question.Options
                .OrderBy(option => option.Position)
                .ToArray();
            int totalQuestionVotes = options.Sum(option =>
                voteCounts.GetValueOrDefault(option.Id));

            questionResults.Add(new PollQuestionResultsResponse(
                question.Id,
                question.Text,
                question.Position,
                options.Select(option =>
                {
                    int voteCount = voteCounts.GetValueOrDefault(option.Id);
                    return new PollOptionResultsResponse(
                        option.Id,
                        option.Text,
                        option.Position,
                        voteCount,
                        CalculatePercentage(voteCount, totalQuestionVotes));
                }).ToArray()));
        }

        return new PollResultsResult(
            PollResultsOutcome.Succeeded,
            new PollResultsResponse(
                poll.Id,
                ToStatus(poll.Status),
                poll.Status == PollStatus.Open ? "interim" : "final",
                totalParticipants,
                questionResults));
    }

    private static decimal CalculatePercentage(int voteCount, int totalVotes)
    {
        if (totalVotes == 0)
        {
            return 0m;
        }

        return decimal.Round(
            voteCount * 100m / totalVotes,
            2,
            MidpointRounding.AwayFromZero);
    }

    private static string ToStatus(PollStatus status) => status switch
    {
        PollStatus.Open => "open",
        PollStatus.Closed => "closed",
        _ => throw new InvalidOperationException(
            $"Results are not available for poll status {status}.")
    };

    private sealed record PollHeader(Guid Id, Guid CreatorId, PollStatus Status);
}
