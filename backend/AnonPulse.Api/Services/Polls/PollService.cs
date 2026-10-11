using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonPulse.Api.Services.Polls;

public sealed class PollService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IPollService
{
    public async Task<PollCreationResult> CreateAsync(
        Guid creatorId,
        CreatePollRequest request,
        CancellationToken cancellationToken = default)
    {
        PollRequestValidation validation = Validate(request);

        if (validation.Errors.Count > 0)
        {
            return new PollCreationResult(null, validation.Errors);
        }

        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            Question = validation.Question!,
            Status = PollStatus.Draft,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime
        };

        for (int position = 0; position < validation.Options.Count; position++)
        {
            poll.Options.Add(new PollOption
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                Text = validation.Options[position],
                Position = position
            });
        }

        dbContext.Polls.Add(poll);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new PollCreationResult(ToResponse(poll), validation.Errors);
    }

    public async Task<IReadOnlyList<PollResponse>> GetAllAsync(
        Guid creatorId,
        CancellationToken cancellationToken = default)
    {
        List<Poll> polls = await dbContext.Polls
            .AsNoTracking()
            .Where(poll => poll.CreatorId == creatorId)
            .Include(poll => poll.Options)
            .OrderByDescending(poll => poll.CreatedAt)
            .ThenByDescending(poll => poll.Id)
            .ToListAsync(cancellationToken);

        return polls.Select(ToResponse).ToArray();
    }

    public async Task<PollResponse?> GetByIdAsync(
        Guid creatorId,
        Guid pollId,
        CancellationToken cancellationToken = default)
    {
        Poll? poll = await dbContext.Polls
            .AsNoTracking()
            .Where(poll => poll.Id == pollId && poll.CreatorId == creatorId)
            .Include(poll => poll.Options)
            .SingleOrDefaultAsync(cancellationToken);

        return poll is null ? null : ToResponse(poll);
    }

    private static PollRequestValidation Validate(CreatePollRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string question = NormalizeWhitespace(request.Question);

        if (question.Length == 0)
        {
            errors[nameof(request.Question)] = ["Question must not be empty."];
        }

        IReadOnlyList<string?> requestedOptions = request.Options ?? [];

        if (requestedOptions.Count < 2)
        {
            errors[nameof(request.Options)] = ["At least two options are required."];
        }

        var normalizedOptions = new List<string>(requestedOptions.Count);
        var uniqueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < requestedOptions.Count; index++)
        {
            string option = NormalizeWhitespace(requestedOptions[index]);
            normalizedOptions.Add(option);

            if (option.Length == 0)
            {
                errors[$"Options[{index}]"] = ["Option text must not be empty."];
                continue;
            }

            if (!uniqueOptions.Add(option))
            {
                errors[$"Options[{index}]"] = ["Options must be unique."];
            }
        }

        return new PollRequestValidation(question, normalizedOptions, errors);
    }

    private static string NormalizeWhitespace(string? value)
    {
        return value is null
            ? string.Empty
            : string.Join(
                ' ',
                value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static PollResponse ToResponse(Poll poll)
    {
        return new PollResponse(
            poll.Id,
            poll.Question,
            poll.Status switch
            {
                PollStatus.Draft => "draft",
                PollStatus.Open => "open",
                PollStatus.Closed => "closed",
                _ => throw new InvalidOperationException($"Unknown poll status: {poll.Status}.")
            },
            poll.CreatedAt,
            poll.PublishedAt,
            poll.ClosedAt,
            poll.Options
                .OrderBy(option => option.Position)
                .Select(option => new PollOptionResponse(
                    option.Id,
                    option.Text,
                    option.Position))
                .ToArray());
    }

    private sealed record PollRequestValidation(
        string Question,
        IReadOnlyList<string> Options,
        IReadOnlyDictionary<string, string[]> Errors);
}
